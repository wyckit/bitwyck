# Tool Authoring

A "tool" is anything the LLM can ask the harness to run. The contract is one
interface:

```csharp
public interface ITool
{
    string Name { get; }              // e.g. "read_file"
    string Description { get; }       // shown to the LLM in the system prompt
    string ArgumentSchema { get; }    // pipe-delimited, e.g. "path|encoding"

    Task<ToolResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}
```

The LLM invokes a tool by emitting:

```
<call>tool_name|arg1|arg2</call>
```

The harness intercepts the tag in the streaming output, looks the tool up in
`IToolRegistry`, and runs `ExecuteAsync(args)`. The result is injected back
into the LLM's context as `<observation>...</observation>` (or
`<observation error="true">...</observation>` on failure).

## Built-in tools

- `read_file|<path>` — file read, allow-list-gated, 8 KB truncated
- `write_file|<path>|<content>` — file write, allow-list-gated
- `list_files|<dir>` or `list_files|<dir>|<glob>` — directory listing
- `run_bash|<command>` — shell command, prefix allow-list-gated, 30-second timeout
- `query_engram|<text>|<namespace?>|<k?>` — engram search
- `store_engram|<id>|<namespace>|<text>` — engram write
- `spawn_agent|task1|task2|...` — fan-out to Reflex_1B sub-agents
- `schedule_task|<cron>|<jobId>|<prompt>` — register a chrono-job

## Authoring a new tool

```csharp
public sealed class HttpGetTool : ITool
{
    private readonly IHttpClientFactory _http;
    public HttpGetTool(IHttpClientFactory http) { _http = http; }

    public string Name => "http_get";
    public string Description => "Fetch a URL and return its body (truncated to 8 KB).";
    public string ArgumentSchema => "url";

    public async Task<ToolResult> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1) return ToolResult.Fail(Name, "missing url argument");
        try
        {
            using var client = _http.CreateClient();
            var body = await client.GetStringAsync(args[0], ct);
            if (body.Length > 8192) body = body[..8192] + "... [truncated]";
            return ToolResult.Ok(Name, body);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(Name, ex.Message);
        }
    }
}
```

Register in `Program.cs`:

```csharp
services.AddSingleton<ITool, HttpGetTool>();
// ToolRegistry is built from all DI'd ITool instances at startup.
```

## Skill synthesis (auto-generated tools)

When the harness observes a successful 4+-step tool chain it hasn't seen
before, `SkillSynthesizer` emits a new `ITool` source file (`Synth_<hex>.cs`),
compiles it via Roslyn, and pushes it through `SkillLifecycle`:

```
Proposed → Sandboxed → UnderReview → Promoted
                                 └→ Rejected
```

Promotion adds the synthesized type to the live `IToolRegistry`. Until then
it's available only inside sandbox runs.

## Safety

- File tools enforce an `allowedRoots` list; paths resolved with
  `Path.GetFullPath` must start (case-insensitive on Windows) with one of the
  allowed roots, or the call is rejected.
- `run_bash` rejects any command that doesn't start with an allow-listed
  prefix. Add prefixes via `Bitwyck:Tools:BashAllowList` in `appsettings.json`.
- The skill synthesizer runs compiled assemblies in the default load context.
  For untrusted code, run them in a separate `AssemblyLoadContext` with
  collectibility (future hardening — currently in-context).
