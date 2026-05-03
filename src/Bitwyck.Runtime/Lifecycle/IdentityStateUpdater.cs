using System.Text;
using System.Text.Json;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bitwyck.Runtime.Lifecycle;

/// <summary>
/// Nightly chrono-job (03:00 daily) that summarises the day's episodic engrams
/// into a revised <see cref="UserIdentityState"/> via the Deliberate_7B model.
/// </summary>
public sealed class IdentityStateUpdater : IChronoJob
{
    // ── IChronoJob ────────────────────────────────────────────────────────────

    public string JobId => "identity-state-updater";

    /// <summary>Fire at minute 0, hour 3, every day, every month, every day-of-week.</summary>
    public string CronExpression => "0 3 * * *";

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IIdentityStore _identityStore;
    private readonly IEngramMemoryStore _engramStore;
    private readonly IBitNetInferenceClient _inference;
    private readonly ILogger<IdentityStateUpdater> _logger;

    private const string EpisodicNamespace = "bitwyck-episodic";

    public IdentityStateUpdater(
        IIdentityStore identityStore,
        IEngramMemoryStore engramStore,
        IBitNetInferenceClient inference,
        ILogger<IdentityStateUpdater> logger)
    {
        _identityStore = identityStore;
        _engramStore   = engramStore;
        _inference     = inference;
        _logger        = logger;
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    public async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("{JobId} starting nightly identity-state update.", JobId);

        // 1. Load current identity state
        UserIdentityState current;
        try
        {
            current = await _identityStore.LoadAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{JobId} failed to load current UserIdentityState — aborting.", JobId);
            return;
        }

        // 2. Retrieve last 24 h of episodic engrams
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        IReadOnlyList<Engram> episodic;
        try
        {
            episodic = await _engramStore.SearchAsync(
                new EngramQuery(
                    Text: "daily interactions observations goals",
                    Namespace: EpisodicNamespace,
                    K: 50,
                    Hybrid: true,
                    ExpandGraph: false),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{JobId} failed to search episodic engrams — aborting.", JobId);
            return;
        }

        // Filter to entries created within the last 24 hours (if timestamp is available)
        var recentEpisodic = episodic
            .Where(e => e.Timestamp is null || e.Timestamp >= since)
            .ToList();

        _logger.LogInformation("{JobId} found {Count} episodic engrams for the update.", JobId, recentEpisodic.Count);

        if (recentEpisodic.Count == 0)
        {
            _logger.LogInformation("{JobId} no new episodic engrams — skipping identity update.", JobId);
            return;
        }

        // 3. Build the summary prompt
        var prompt = BuildPrompt(current, recentEpisodic);

        // 4. Call the LLM
        InferenceResponse response;
        try
        {
            response = await _inference.CompleteAsync(
                new InferenceRequest(
                    Tier: ModelTier.Deliberate_7B,
                    Messages: new[]
                    {
                        new InferenceMessage(MessageRole.System,
                            "You are a cognitive identity synthesizer. " +
                            "Output ONLY a valid JSON object — no prose before or after."),
                        new InferenceMessage(MessageRole.User, prompt)
                    },
                    MaxTokens: 1024,
                    Temperature: 0.2),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{JobId} inference call failed — aborting identity update.", JobId);
            return;
        }

        // 5. JSON-tolerant parse: find first '{' and last '}'
        UserIdentityState? updated = TryParseIdentityState(response.Content);

        if (updated is null)
        {
            _logger.LogWarning(
                "{JobId} could not parse LLM output as UserIdentityState — skipping update. Raw: {Raw}",
                JobId, response.Content);
            return;
        }

        // 6. Stamp version and timestamp, then persist
        updated = updated with
        {
            Version     = current.Version + 1,
            LastUpdated = DateTimeOffset.UtcNow
        };

        try
        {
            await _identityStore.SaveAsync(updated, ct);
            _logger.LogInformation(
                "{JobId} identity state updated to version {Version} at {Time}.",
                JobId, updated.Version, updated.LastUpdated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{JobId} failed to persist updated UserIdentityState.", JobId);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildPrompt(UserIdentityState current, IReadOnlyList<Engram> episodic)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Current Identity State");
        sb.AppendLine(current.ToPromptBlock());

        sb.AppendLine("## Today's Episodic Observations");
        foreach (var e in episodic)
        {
            var ts = e.Timestamp.HasValue
                ? e.Timestamp.Value.ToString("HH:mm")
                : "??:??";
            sb.AppendLine($"[{ts}] {e.Text}");
        }

        sb.AppendLine();
        sb.AppendLine("""
            ## Task
            Based on the episodic observations above, update the identity state to reflect any new
            preferences, goals, constraints, or known facts that emerged today.

            Return a JSON object with exactly these keys:
            {
              "Persona": "<string>",
              "Preferences": ["<string>", ...],
              "EvolvingGoals": ["<string>", ...],
              "Constraints": ["<string>", ...],
              "KnownFacts": { "<key>": "<value>", ... }
            }

            Do NOT include "Version" or "LastUpdated" — those will be set by the system.
            Output ONLY the JSON object, no markdown fences or explanations.
            """);

        return sb.ToString();
    }

    /// <summary>
    /// Tolerant JSON extraction: finds the first '{' and last '}' in the LLM output,
    /// then attempts to deserialize.  Returns null on any failure.
    /// </summary>
    private UserIdentityState? TryParseIdentityState(string rawOutput)
    {
        int start = rawOutput.IndexOf('{');
        int end   = rawOutput.LastIndexOf('}');

        if (start < 0 || end < start)
        {
            _logger.LogDebug("{JobId} no JSON object delimiters found in LLM output.", JobId);
            return null;
        }

        var json = rawOutput[start..(end + 1)];

        try
        {
            var dto = JsonSerializer.Deserialize<IdentityStateDto>(json, _jsonOptions);
            if (dto is null)
                return null;

            return new UserIdentityState(
                Persona:       dto.Persona ?? "Unknown user — observe and adapt.",
                Preferences:   (IReadOnlyList<string>?)dto.Preferences   ?? Array.Empty<string>(),
                EvolvingGoals: (IReadOnlyList<string>?)dto.EvolvingGoals  ?? Array.Empty<string>(),
                Constraints:   (IReadOnlyList<string>?)dto.Constraints   ?? Array.Empty<string>(),
                KnownFacts:    (IReadOnlyDictionary<string, string>?)dto.KnownFacts ?? new Dictionary<string, string>(),
                LastUpdated:   DateTimeOffset.UtcNow,  // will be overwritten by caller
                Version:       0);                      // will be overwritten by caller
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "{JobId} JSON deserialization failed.", JobId);
            return null;
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip
    };

    // ── Private DTO ───────────────────────────────────────────────────────────

    /// <summary>Intermediate deserialization target — permissive nullability.</summary>
    private sealed class IdentityStateDto
    {
        public string?                      Persona       { get; set; }
        public List<string>?               Preferences   { get; set; }
        public List<string>?               EvolvingGoals { get; set; }
        public List<string>?               Constraints   { get; set; }
        public Dictionary<string, string>? KnownFacts    { get; set; }
    }
}
