using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;
using Microsoft.Extensions.DependencyInjection;

namespace Bitwyck.CLI.Commands;

public sealed class AskCommand
{
    private readonly IServiceProvider _services;
    public AskCommand(IServiceProvider services) { _services = services; }

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var prompt = string.Join(' ', args).Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            Console.Error.WriteLine("usage: bitwyck ask <your prompt>");
            return 2;
        }

        var loop = _services.GetRequiredService<CognitiveLoop>();
        var ev = SensoryEvent.FromText(prompt, source: "cli:ask");
        var result = await loop.RunAsync(ev, ct);

        Console.WriteLine(result.FinalAnswer);
        if (result.DegradedMode)
            Console.Error.WriteLine($"[degraded: {result.DegradedReason}]");
        if (result.ToolCalls.Count > 0)
            Console.Error.WriteLine($"[tools used: {string.Join(", ", result.ToolCalls.Select(t => t.ToolName))}]");
        return result.DegradedMode ? 1 : 0;
    }
}
