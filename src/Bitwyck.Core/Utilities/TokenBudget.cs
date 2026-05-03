namespace Bitwyck.Core.Utilities;

/// <summary>
/// Cheap byte-based token estimator. Approximates a tokenizer to ~4 bytes/token
/// for English; good enough for budget gating without loading an actual tokenizer.
/// </summary>
public static class TokenBudget
{
    public const double BytesPerToken = 4.0;

    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // Use UTF-8 byte count divided by ~4. Round up so we never under-budget.
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        return (int)Math.Ceiling(bytes / BytesPerToken);
    }

    public static int Estimate(IEnumerable<string> texts)
    {
        var total = 0;
        foreach (var t in texts) total += Estimate(t);
        return total;
    }

    /// <summary>Greedy truncate: keep prefix that fits in the budget. Returns the prefix.</summary>
    public static string Truncate(string text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text) || maxTokens <= 0) return string.Empty;
        var allowedBytes = (int)(maxTokens * BytesPerToken);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= allowedBytes) return text;
        // Safe-truncate: clip to allowedBytes then back off until we're at a valid UTF-8 boundary.
        var clip = allowedBytes;
        while (clip > 0 && (bytes[clip] & 0xC0) == 0x80) clip--;
        return System.Text.Encoding.UTF8.GetString(bytes, 0, clip);
    }
}
