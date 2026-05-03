using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;
using Microsoft.Extensions.DependencyInjection;

namespace Bitwyck.CLI.Commands;

public sealed class ReplCommand
{
    private readonly IServiceProvider _services;
    public ReplCommand(IServiceProvider services) { _services = services; }

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var loop = _services.GetRequiredService<CognitiveLoop>();
        Console.WriteLine("bitwyck repl — type :quit to exit, :help for commands.");
        while (!ct.IsCancellationRequested)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            switch (line.ToLowerInvariant())
            {
                case ":quit" or ":q" or ":exit":
                    return 0;
                case ":help":
                    Console.WriteLine(":quit  exit");
                    Console.WriteLine(":help  show this");
                    continue;
            }

            try
            {
                var ev = SensoryEvent.FromText(line, source: "cli:repl");
                var result = await loop.RunAsync(ev, ct);
                Console.WriteLine(result.FinalAnswer);
                if (result.DegradedMode)
                    Console.Error.WriteLine($"[degraded: {result.DegradedReason}]");
            }
            catch (OperationCanceledException) { return 0; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[error: {ex.Message}]");
            }
        }
        return 0;
    }
}
