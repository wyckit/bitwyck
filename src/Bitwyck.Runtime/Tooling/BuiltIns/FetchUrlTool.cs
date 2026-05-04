using System.Net;
using System.Text.RegularExpressions;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;

namespace Bitwyck.Runtime.Tooling.BuiltIns;

/// <summary>
/// <c>fetch_url|&lt;url&gt;[|&lt;maxChars&gt;]</c>
/// HTTP GET the URL, strip HTML to readable text, return up to <c>maxChars</c>
/// (default 6000) characters. Used by the agent to summarise web articles
/// without spending model tokens on the full page source.
/// </summary>
public sealed class FetchUrlTool : ITool
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly int _defaultMax;
    private readonly TimeSpan _timeout;

    public FetchUrlTool(IHttpClientFactory httpFactory, int defaultMaxChars = 6000, TimeSpan? timeout = null)
    {
        _httpFactory = httpFactory;
        _defaultMax = defaultMaxChars;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public string Name => "fetch_url";
    public string Description => "Fetches an HTTP URL and returns its readable text content (HTML stripped, output capped).";
    public string ArgumentSchema => "url|maxChars?";

    public async Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (arguments.Count < 1 || string.IsNullOrWhiteSpace(arguments[0]))
            return ToolResult.Fail(Name, "missing url argument");

        var url = arguments[0].Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return ToolResult.Fail(Name, $"invalid http(s) url: {url}");

        var maxChars = _defaultMax;
        if (arguments.Count >= 2 && int.TryParse(arguments[1], out var parsed))
            maxChars = Math.Clamp(parsed, 256, 32_000);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("bitwyck/0.1 (+https://github.com/wyckit/bitwyck)");

            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
                return ToolResult.Fail(Name, $"http {(int)resp.StatusCode} {resp.ReasonPhrase}");

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "text/html";
            var raw = await resp.Content.ReadAsStringAsync(cts.Token);

            var text = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                ? StripHtml(raw)
                : raw;

            // Squeeze repeated whitespace, then cap.
            text = Whitespace.Replace(text, " ").Trim();
            var truncated = text.Length > maxChars;
            if (truncated) text = text[..maxChars] + "... [truncated]";

            return ToolResult.Ok(Name, $"[{(int)resp.StatusCode}] {uri.Host}{uri.AbsolutePath}\n\n{text}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Fail(Name, $"timed out after {_timeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(Name, ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // HTML -> text (lightweight, no external deps)
    // -------------------------------------------------------------------------

    private static readonly Regex Script = new(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Style  = new(@"<style\b[^>]*>.*?</style>",   RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Tags   = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        html = Script.Replace(html, " ");
        html = Style.Replace(html, " ");
        html = Tags.Replace(html, " ");
        return WebUtility.HtmlDecode(html);
    }
}
