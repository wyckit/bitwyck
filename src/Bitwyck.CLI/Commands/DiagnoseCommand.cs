using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Inference;
using Bitwyck.Runtime.Memory;
using Bitwyck.Runtime.Sensors;
using Microsoft.Extensions.DependencyInjection;

namespace Bitwyck.CLI.Commands;

public sealed class DiagnoseCommand
{
    private readonly IServiceProvider _services;
    public DiagnoseCommand(IServiceProvider services) { _services = services; }

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        Console.WriteLine("bitwyck diagnose");
        Console.WriteLine("================");

        var ok = true;
        ok &= ReportFile("BitNet binary",
            Path.Combine(_services.GetRequiredService<BitNetOptions>().BinDirectory,
                _services.GetRequiredService<BitNetOptions>().ServerExeName));

        var bnOpts = _services.GetRequiredService<BitNetOptions>();
        foreach (var (tier, cfg) in bnOpts.Tiers)
            ok &= ReportFile($"Model {tier}", cfg.ModelPath);

        // Engram store (smoke test: read count)
        try
        {
            var store = _services.GetRequiredService<IEngramMemoryStore>();
            var count = await store.CountAsync(null, ct);
            Console.WriteLine($"[ ok ] Engram store reachable; total entries: {count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[fail] Engram store: {ex.Message}");
            ok = false;
        }

        // Tool registry
        var reg = _services.GetRequiredService<IToolRegistry>();
        var tools = reg.All();
        Console.WriteLine($"[ ok ] Tool registry: {tools.Count} tools — {string.Join(", ", tools.Select(t => t.Name))}");

        // Sensors
        Console.WriteLine($"[ ok ] TextSensor: available");
        var vision = _services.GetRequiredService<VisionSensor>();
        Console.WriteLine($"[{(await vision.IsAvailableAsync(ct) ? " ok " : "skip")}] VisionSensor (LLaVA)");
        var whisper = _services.GetRequiredService<WhisperSensor>();
        Console.WriteLine($"[{(await whisper.IsAvailableAsync(ct) ? " ok " : "skip")}] WhisperSensor (audio)");

        Console.WriteLine();
        Console.WriteLine(ok ? "All required subsystems healthy." : "Some required subsystems are unhealthy — see above.");
        return ok ? 0 : 1;
    }

    private static bool ReportFile(string label, string path)
    {
        var exists = File.Exists(path);
        Console.WriteLine($"[{(exists ? " ok " : "miss")}] {label}: {path}");
        return exists;
    }
}
