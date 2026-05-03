using System.Reflection;
using System.Runtime.Loader;
using Bitwyck.Core.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace Bitwyck.Runtime.Lifecycle;

/// <summary>
/// Isolated Roslyn compilation helper. Compiles a C# source string into a
/// loadable <see cref="ITool"/> instance. Kept separate from
/// <see cref="SkillSynthesizer"/> so it can be unit-tested independently.
/// </summary>
public sealed class SkillCompiler
{
    private readonly ILogger<SkillCompiler> _logger;

    public SkillCompiler(ILogger<SkillCompiler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Compiles <paramref name="source"/> and instantiates the type named
    /// <paramref name="typeName"/> (qualified or simple). The type must have a
    /// constructor that accepts a single <see cref="IToolRegistry"/>.
    /// </summary>
    /// <returns>
    /// <c>(tool, diagnostics)</c> — <c>tool</c> is <c>null</c> on failure;
    /// <c>diagnostics</c> contains Roslyn error/warning messages.
    /// </returns>
    public async Task<(ITool? tool, IReadOnlyList<string> diagnostics)> CompileToToolAsync(
        string source,
        string typeName,
        IToolRegistry registry,
        CancellationToken ct = default)
    {
        // Yield so callers composing many compilations don't block the thread.
        await Task.Yield();

        var syntaxTree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);

        var references = BuildReferences();

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            nullableContextOptions: NullableContextOptions.Enable);

        var compilation = CSharpCompilation.Create(
            assemblyName: $"BitwyckSynth_{typeName}_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: references,
            options: options);

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream, cancellationToken: ct);

        var diagnosticMessages = emitResult.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => d.ToString())
            .ToList();

        if (!emitResult.Success)
        {
            foreach (var msg in diagnosticMessages)
                _logger.LogError("Roslyn: {Diagnostic}", msg);

            return (null, diagnosticMessages);
        }

        // Log warnings even on success.
        foreach (var msg in diagnosticMessages)
            _logger.LogWarning("Roslyn: {Diagnostic}", msg);

        peStream.Seek(0, SeekOrigin.Begin);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(peStream);

        // Resolve the type — accept fully-qualified or simple name.
        var toolType = assembly.GetTypes()
            .FirstOrDefault(t =>
                typeof(ITool).IsAssignableFrom(t) &&
                !t.IsAbstract &&
                (t.FullName == typeName || t.Name == typeName));

        if (toolType is null)
        {
            var err = $"Type '{typeName}' implementing ITool was not found in the compiled assembly.";
            _logger.LogError("{Error}", err);
            return (null, diagnosticMessages.Append(err).ToList());
        }

        // Construct via IToolRegistry constructor.
        try
        {
            var instance = Activator.CreateInstance(toolType, registry);
            if (instance is ITool tool)
                return (tool, diagnosticMessages);

            var err = $"Activator returned null or non-ITool for type '{typeName}'.";
            _logger.LogError("{Error}", err);
            return (null, diagnosticMessages.Append(err).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to instantiate synthesized tool type '{TypeName}'.", typeName);
            return (null, diagnosticMessages.Append(ex.Message).ToList());
        }
    }

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    private static List<MetadataReference> BuildReferences()
    {
        // Use trusted-platform-assemblies to cover all BCL + runtime assemblies on
        // .NET 10 without guessing individual paths.
        var trustedPaths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?.Split(Path.PathSeparator)
            ?? Array.Empty<string>();

        var refs = trustedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        // Always include Bitwyck.Core so the generated skill can reference ITool etc.
        var coreLocation = typeof(ITool).Assembly.Location;
        if (!string.IsNullOrEmpty(coreLocation))
            refs.Add(MetadataReference.CreateFromFile(coreLocation));

        return refs;
    }
}
