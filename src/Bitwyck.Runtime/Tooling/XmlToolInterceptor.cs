using System.Text;
using Bitwyck.Core.Models;
using Bitwyck.Core.Utilities;

namespace Bitwyck.Runtime.Tooling;

/// <summary>
/// Streaming state-machine wrapper that detects <c>&lt;call&gt;…&lt;/call&gt;</c> blocks
/// in an <see cref="IAsyncEnumerable{InferenceTokenChunk}"/> token stream, invokes the
/// caller-supplied <see cref="ToolHandler"/> for each complete block, and returns an
/// <see cref="InterceptionResult"/> whose <see cref="InterceptionResult.AssembledOutput"/>
/// has every tool-call block replaced by its matching <c>&lt;observation&gt;…&lt;/observation&gt;</c>.
/// </summary>
/// <remarks>
/// State machine invariants:
/// <list type="bullet">
///   <item>No tokens are dropped — everything is either flushed to output or held in the
///         pending buffer and eventually flushed.</item>
///   <item>Nesting (inner <c>&lt;call&gt;</c> inside the argument string) is handled because
///         we scan for the <em>first</em> <c>&lt;/call&gt;</c> after the opening tag. This
///         matches the spec: the outer call is detected and the inner literal becomes part of
///         the argument string.</item>
///   <item>Malformed / unterminated tags are passed through unchanged.</item>
/// </list>
/// </remarks>
public sealed class XmlToolInterceptor
{
    private const string OpenTag  = "<call>";
    private const string CloseTag = "</call>";

    public delegate Task<ToolResult> ToolHandler(ToolCall call, CancellationToken ct);

    public async Task<InterceptionResult> ProcessAsync(
        IAsyncEnumerable<InferenceTokenChunk> stream,
        ToolHandler handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(handler);

        // Final assembled output (with tool blocks replaced by observations).
        var output = new StringBuilder();

        // Sliding look-ahead buffer. Grows as we accumulate potential tag fragments
        // and shrinks as we flush confirmed non-tag content.
        var buffer = new StringBuilder();

        var detectedCalls    = new List<ToolCall>();
        var detectedResults  = new List<ToolResult>();

        await foreach (var chunk in stream.WithCancellation(ct).ConfigureAwait(false))
        {
            buffer.Append(chunk.Token);
            await DrainBuffer(buffer, output, detectedCalls, detectedResults, handler, ct, flush: false)
                .ConfigureAwait(false);
        }

        // End of stream — flush whatever remains in the buffer.
        await DrainBuffer(buffer, output, detectedCalls, detectedResults, handler, ct, flush: true)
            .ConfigureAwait(false);

        return new InterceptionResult(
            output.ToString(),
            detectedCalls.AsReadOnly(),
            detectedResults.AsReadOnly());
    }

    // ── Core drain logic ──────────────────────────────────────────────────────

    private static async Task DrainBuffer(
        StringBuilder buffer,
        StringBuilder output,
        List<ToolCall>   calls,
        List<ToolResult> results,
        ToolHandler      handler,
        CancellationToken ct,
        bool flush)
    {
        // We loop because a single buffer top-up may contain multiple complete tags.
        while (true)
        {
            var text = buffer.ToString();

            // ── Look for a complete <call>...</call> block ─────────────────────
            int openIdx = text.IndexOf(OpenTag, StringComparison.Ordinal);

            if (openIdx == -1)
            {
                // No open-tag anywhere. Safe to flush everything except the trailing
                // fragment that could be the start of "<call>" (up to 6 chars).
                if (flush)
                {
                    output.Append(text);
                    buffer.Clear();
                }
                else
                {
                    // Keep up to (OpenTag.Length - 1) chars as a look-ahead guard.
                    int safeLen = Math.Max(0, text.Length - (OpenTag.Length - 1));
                    if (safeLen > 0)
                    {
                        output.Append(text, 0, safeLen);
                        buffer.Remove(0, safeLen);
                    }
                }
                return;
            }

            // We found "<call>" starts at openIdx.
            // Flush everything BEFORE the open tag to output — it is confirmed non-tag content.
            if (openIdx > 0)
            {
                output.Append(text, 0, openIdx);
                buffer.Remove(0, openIdx);
                text = buffer.ToString(); // rebind
            }

            // Now buffer starts with "<call>".
            int closeIdx = text.IndexOf(CloseTag, OpenTag.Length, StringComparison.Ordinal);

            if (closeIdx == -1)
            {
                // No closing tag yet. If we're flushing (stream ended), pass through as-is.
                if (flush)
                {
                    output.Append(text);
                    buffer.Clear();
                }
                // Otherwise keep accumulating — do nothing and return.
                return;
            }

            // We have a complete <call>inner</call> block.
            int blockEnd   = closeIdx + CloseTag.Length;
            string rawBlock  = text[..blockEnd];               // "<call>...</call>"
            string innerText = text[OpenTag.Length..closeIdx]; // content between tags

            // Remove the block from the buffer and advance past it.
            buffer.Remove(0, blockEnd);

            // Parse and invoke, or pass through if malformed.
            if (XmlCallParser.TryParseInner(innerText, rawBlock, out var call) && call is not null)
            {
                ToolResult result;
                try
                {
                    result = await handler(call, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = ToolResult.Fail(call.ToolName, $"Handler threw: {ex.Message}");
                }

                calls.Add(call);
                results.Add(result);
                output.Append(result.ToObservation());
            }
            else
            {
                // Malformed inner content — pass through verbatim.
                output.Append(rawBlock);
            }

            // Loop back — there may be more complete blocks now in the buffer.
        }
    }
}

/// <summary>
/// The result returned by <see cref="XmlToolInterceptor.ProcessAsync"/>.
/// </summary>
public sealed record InterceptionResult(
    string AssembledOutput,
    IReadOnlyList<ToolCall>   ToolCalls,
    IReadOnlyList<ToolResult> ToolResults);
