using Bitwyck.CLI.Commands;
using Bitwyck.Core.Interfaces;
using Bitwyck.Core.Models;
using Bitwyck.Runtime.Cognition;
using Bitwyck.Runtime.Inference;
using Bitwyck.Runtime.Lifecycle;
using Bitwyck.Runtime.Memory;
using Bitwyck.Runtime.Sensors;
using Bitwyck.Runtime.Tooling;
using Bitwyck.Runtime.Tooling.BuiltIns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bitwyck.CLI;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

        if (verb is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        using var host = BuildHost(args).Build();

        // Daemon owns its own lifecycle (host.RunAsync handles Start/Stop).
        // For one-shot commands we must explicitly Start/Stop so IHostedServices fire.
        if (verb == "daemon")
            return await new DaemonCommand(host).RunAsync(rest);

        await host.StartAsync();
        try
        {
            var ct = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            return verb switch
            {
                "ask" => await new AskCommand(host.Services).RunAsync(rest, ct),
                "repl" => await new ReplCommand(host.Services).RunAsync(rest, ct),
                "agent" => await new AgentCommand(host.Services).RunAsync(rest, ct),
                "chat" => await new ChatCommand(host.Services).RunAsync(rest, ct),
                "diagnose" => await new DiagnoseCommand(host.Services).RunAsync(rest, ct),
                _ => UnknownVerb(verb),
            };
        }
        finally
        {
            await host.StopAsync();
        }

        static int UnknownVerb(string verb)
        {
            Console.Error.WriteLine($"Unknown command: {verb}");
            PrintHelp();
            return 2;
        }
    }

    private static IHostBuilder BuildHost(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(c =>
            {
                var assemblyDir = AppContext.BaseDirectory;
                c.AddJsonFile(Path.Combine(assemblyDir, "appsettings.json"), optional: true, reloadOnChange: false);
                c.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                c.AddEnvironmentVariables(prefix: "BITWYCK_");
            })
            .ConfigureLogging(lb =>
            {
                lb.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
            })
            .ConfigureServices((ctx, services) =>
            {
                ConfigureServices(ctx.Configuration, services);
            });
    }

    private static void ConfigureServices(IConfiguration config, IServiceCollection services)
    {
        // ----- Options -----
        services.AddSingleton(sp =>
        {
            var opts = BitNetOptions.Default();
            // Apply optional overrides from configuration if present.
            var section = config.GetSection("Bitwyck:BitNet");
            if (section.Exists())
            {
                opts = opts with
                {
                    BinDirectory = section["BinDirectory"] ?? opts.BinDirectory,
                    ServerHost = section["ServerHost"] ?? opts.ServerHost,
                    DefaultThreads = int.TryParse(section["DefaultThreads"], out var t) ? t : opts.DefaultThreads,
                    DefaultContextSize = int.TryParse(section["DefaultContextSize"], out var cs) ? cs : opts.DefaultContextSize,
                    MaxPromptChars = int.TryParse(section["MaxPromptChars"], out var mp) ? mp : opts.MaxPromptChars,
                    RopeScalingType = section["RopeScalingType"] ?? opts.RopeScalingType,
                    RopeFreqScale = double.TryParse(section["RopeFreqScale"], out var rs) ? rs : opts.RopeFreqScale,
                };
            }
            return opts;
        });

        services.AddSingleton(sp =>
        {
            var section = config.GetSection("Bitwyck:Engram");
            return new EngramOptions
            {
                DatabasePath = section["DatabasePath"] ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "bitwyck", "engram.db"),
                EmbeddingModelPath = section["EmbeddingModelPath"],
                DefaultNamespace = section["DefaultNamespace"] ?? "bitwyck-episodic",
            };
        });

        services.AddHttpClient();

        // ----- Inference -----
        services.AddSingleton<BitNetServerHost>();
        if (config.GetValue("Bitwyck:BitNet:UseServer", false))
            services.AddHostedService(sp => sp.GetRequiredService<BitNetServerHost>());
        services.AddSingleton<BitNetServerClient>();
        services.AddSingleton<BitNetCliClient>();
        // Default IBitNetInferenceClient is the CLI shell-out path. The HTTP server has
        // a known stack-overflow crash with the current BitNet kernels in this llama-server
        // build; CLI works reliably. To switch back, set Bitwyck:BitNet:UseServer=true.
        services.AddSingleton<IBitNetInferenceClient>(sp =>
        {
            var useServer = config.GetValue("Bitwyck:BitNet:UseServer", false);
            return useServer
                ? (IBitNetInferenceClient)sp.GetRequiredService<BitNetServerClient>()
                : sp.GetRequiredService<BitNetCliClient>();
        });
        services.AddSingleton<ICognitiveRouter>(sp =>
        {
            var forced = config["Bitwyck:BitNet:ForceTier"];
            if (!string.IsNullOrWhiteSpace(forced) && Enum.TryParse<ModelTier>(forced, out var tier))
                return new ForcedTierRouter(tier);
            return new EnergyManager();
        });

        // ----- Memory -----
        services.AddSingleton<EngramAdapter>();
        services.AddSingleton<IEngramMemoryStore>(sp => sp.GetRequiredService<EngramAdapter>());
        services.AddSingleton<EngramRecallService>();
        services.AddSingleton<EngramCommitService>();

        services.AddSingleton<IIdentityStore>(sp =>
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "bitwyck", "identity.json");
            return new IdentityStateStoreAdapter(new IdentityStateStore(path));
        });

        // ----- Tooling -----
        services.AddSingleton<IToolRegistry>(sp =>
        {
            var reg = new ToolRegistry();
            var allowedRoots = config.GetSection("Bitwyck:Tools:AllowedRoots").Get<string[]>()
                ?? new[] { Environment.CurrentDirectory };
            var bashAllowList = config.GetSection("Bitwyck:Tools:BashAllowList").Get<string[]>()
                ?? new[] { "echo", "dir", "git status", "git log", "dotnet --version", "dotnet --info", "dotnet --list-sdks", "ls" };

            reg.Register(new ReadFileTool(allowedRoots));
            reg.Register(new WriteFileTool(allowedRoots));
            reg.Register(new ListFilesTool(allowedRoots));
            reg.Register(new RunBashTool(bashAllowList));
            reg.Register(new QueryEngramTool(sp.GetRequiredService<IEngramMemoryStore>()));
            reg.Register(new StoreEngramTool(sp.GetRequiredService<IEngramMemoryStore>()));
            reg.Register(new SpawnAgentTool(sp.GetRequiredService<ParallelCognitiveDispatcher>()));
            reg.Register(new FetchUrlTool(sp.GetRequiredService<IHttpClientFactory>()));
            reg.Register(new SummarizeTool(sp.GetRequiredService<IBitNetInferenceClient>(), sp.GetRequiredService<BitNetOptions>()));
            reg.Register(new MapReduceTool(sp.GetRequiredService<IBitNetInferenceClient>(), sp.GetRequiredService<BitNetOptions>()));
            // ScheduleTaskTool is registered after the loop is constructed (circular dep) — handled in Daemon.
            return reg;
        });

        // ----- Cognition -----
        services.AddSingleton<ISystemBiasProvider, DefaultSystemBiasProvider>();
        services.AddSingleton<PromptCompiler>();
        services.AddSingleton<ParallelCognitiveDispatcher>(sp =>
            new ParallelCognitiveDispatcher(sp.GetRequiredService<IBitNetInferenceClient>()));

        services.AddSingleton<IntentDispatcher>(sp => new IntentDispatcher(
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<IEngramMemoryStore>(),
            sp.GetRequiredService<ILogger<IntentDispatcher>>()));

        services.AddSingleton<CognitiveLoop>(sp =>
        {
            var inference = sp.GetRequiredService<IBitNetInferenceClient>();
            var router = sp.GetRequiredService<ICognitiveRouter>();
            var bias = sp.GetRequiredService<ISystemBiasProvider>();
            var tools = sp.GetRequiredService<IToolRegistry>();
            var compiler = sp.GetRequiredService<PromptCompiler>();
            var recallSvc = sp.GetRequiredService<EngramRecallService>();
            var commitSvc = sp.GetRequiredService<EngramCommitService>();
            var identity = sp.GetRequiredService<IIdentityStore>();
            var logger = sp.GetRequiredService<ILogger<CognitiveLoop>>();

            CognitiveLoop.RecallFn recall = (ev, ident, ct) => recallSvc.RecallAsync(ev, ident, ct: ct);
            CognitiveLoop.InterceptFn intercept = async (stream, handler, ct) =>
            {
                var interceptor = new XmlToolInterceptor();
                var result = await interceptor.ProcessAsync(
                    stream,
                    new XmlToolInterceptor.ToolHandler((c, t) => handler(c, t)),
                    ct);

                // Post-LLM salvage: if the canonical interceptor caught nothing,
                // try the multi-format extractor (JSON, brackets, function-call,
                // fuzzy tool name match) on the assembled output.
                if (result.ToolCalls.Count == 0)
                {
                    var salvaged = ToolCallExtractor.Extract(result.AssembledOutput, tools);
                    if (salvaged.Count > 0)
                    {
                        var extraResults = new List<ToolResult>();
                        var assembled = result.AssembledOutput;
                        foreach (var call in salvaged)
                        {
                            var tr = await handler(call, ct);
                            extraResults.Add(tr);
                            assembled += "\n" + tr.ToObservation();
                        }
                        return new InterceptionOutcome(assembled, salvaged, extraResults);
                    }
                }

                return new InterceptionOutcome(result.AssembledOutput, result.ToolCalls, result.ToolResults);
            };
            CognitiveLoop.CommitFn commit = (cycle, ct) => commitSvc.CommitAsync(cycle, ct);

            return new CognitiveLoop(inference, router, bias, tools, compiler, recall, intercept, commit, identity);
        });

        // ----- Lifecycle -----
        services.AddSingleton<ChronoScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<ChronoScheduler>());
        services.AddSingleton<IdentityStateUpdater>();

        // ----- Sensors -----
        services.AddSingleton<TextSensor>();
        services.AddSingleton<WebhookSensor>(sp => new WebhookSensor());
        services.AddSingleton<WhisperSensor>(sp =>
        {
            var path = config["Bitwyck:Audio:WhisperModelPath"];
            return new WhisperSensor(path);
        });
        services.AddSingleton<VisionSensor>(sp =>
        {
            var llava = config["Bitwyck:Vision:LlavaCliPath"]
                ?? @"C:/Software/research/BitNet/build/bin/Release/llama-llava-cli.exe";
            var model = config["Bitwyck:Vision:LlavaModelPath"] ?? string.Empty;
            var mmproj = config["Bitwyck:Vision:MmprojPath"] ?? string.Empty;
            return new VisionSensor(llava, model, mmproj);
        });
    }

    private static void PrintHelp()
    {
        Console.WriteLine("bitwyck — Autonomous Cognitive Harness");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  bitwyck <command> [args]");
        Console.WriteLine();
        Console.WriteLine("COMMANDS:");
        Console.WriteLine("  ask <prompt>          Run one cognitive turn and print the answer.");
        Console.WriteLine("  repl                  Interactive raw LLM shell (no shortcuts).");
        Console.WriteLine("  chat                  Multi-turn chat with the full agent stack (shortcuts + engram).");
        Console.WriteLine("  agent [--max N] <goal>  Iterative reason->tool->observe agent loop.");
        Console.WriteLine("  daemon                Start chrono-scheduler + sensors as a long-running process.");
        Console.WriteLine("  diagnose              Health-check every subsystem.");
        Console.WriteLine("  help            This message.");
    }
}

/// <summary>
/// Adapts the Memory subsystem's <see cref="IdentityStateStore"/> (concrete file-backed)
/// to the Lifecycle subsystem's <see cref="IIdentityStore"/> abstraction so the
/// CognitiveLoop can depend on the latter.
/// </summary>
internal sealed class IdentityStateStoreAdapter : IIdentityStore
{
    private readonly IdentityStateStore _inner;
    public IdentityStateStoreAdapter(IdentityStateStore inner) { _inner = inner; }

    public Task<UserIdentityState> LoadAsync(CancellationToken ct = default) => _inner.LoadAsync(ct);
    public Task SaveAsync(UserIdentityState state, CancellationToken ct = default) => _inner.SaveAsync(state, ct);
}
