using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;
using Bitwyck.Runtime.Lifecycle;
using Bitwyck.Runtime.Tooling;
using Bitwyck.Runtime.Tooling.BuiltIns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bitwyck.CLI.Commands;

public sealed class DaemonCommand
{
    private readonly IHost _host;
    public DaemonCommand(IHost host) { _host = host; }

    public async Task<int> RunAsync(string[] args)
    {
        // Wire the chrono-loop self-trigger tool now that the CognitiveLoop is built.
        var registry = _host.Services.GetRequiredService<IToolRegistry>() as ToolRegistry;
        var scheduler = _host.Services.GetRequiredService<ChronoScheduler>();
        var loop = _host.Services.GetRequiredService<CognitiveLoop>();
        var identityUpdater = _host.Services.GetRequiredService<IdentityStateUpdater>();

        if (registry is not null)
        {
            registry.Register(new ScheduleTaskTool(scheduler,
                async (ev, ct) => { await loop.RunAsync(ev, ct); }));
        }
        scheduler.Register(identityUpdater);

        Console.WriteLine("bitwyck daemon started — chrono scheduler + sensors are running.");
        Console.WriteLine("Press Ctrl+C to stop.");
        await _host.RunAsync();
        return 0;
    }
}
