using Bitwyck.Runtime.Tooling.BuiltIns;

namespace Bitwyck.Tests.Tooling;

public class BuiltInToolsTests : IDisposable
{
    private readonly string _tempRoot;

    public BuiltInToolsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"BitwyckTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    // ── ReadFileTool ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadFile_ReadsTempFile_ReturnsContent()
    {
        var file = Path.Combine(_tempRoot, "hello.txt");
        await File.WriteAllTextAsync(file, "hello from test");

        var tool = new ReadFileTool(new[] { _tempRoot });
        var result = await tool.ExecuteAsync(new[] { file });

        Assert.True(result.Success);
        Assert.Contains("hello from test", result.Output);
    }

    [Fact]
    public async Task ReadFile_PathOutsideAllowedRoots_Fails()
    {
        var tool = new ReadFileTool(new[] { _tempRoot });
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside_allowed.txt");

        var result = await tool.ExecuteAsync(new[] { outsidePath });

        Assert.False(result.Success);
        Assert.Contains("outside the allowed roots", result.Error);
    }

    [Fact]
    public async Task ReadFile_EmptyAllowedRoots_DeniesAll()
    {
        var tool = new ReadFileTool(Array.Empty<string>());
        var file = Path.Combine(_tempRoot, "test.txt");

        var result = await tool.ExecuteAsync(new[] { file });

        Assert.False(result.Success);
    }

    // ── WriteFileTool ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteFile_WritesToTempFile()
    {
        var file = Path.Combine(_tempRoot, "out.txt");
        var tool = new WriteFileTool(new[] { _tempRoot });

        var result = await tool.ExecuteAsync(new[] { file, "written content" });

        Assert.True(result.Success);
        Assert.True(File.Exists(file));
        Assert.Equal("written content", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task WriteFile_PathOutsideAllowedRoots_Fails()
    {
        var tool = new WriteFileTool(new[] { _tempRoot });
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside_write.txt");

        var result = await tool.ExecuteAsync(new[] { outsidePath, "content" });

        Assert.False(result.Success);
        Assert.Contains("outside the allowed roots", result.Error);
    }

    [Fact]
    public async Task WriteFile_CreatesParentDirectories()
    {
        var nested = Path.Combine(_tempRoot, "subdir", "deep", "file.txt");
        var tool = new WriteFileTool(new[] { _tempRoot });

        var result = await tool.ExecuteAsync(new[] { nested, "deep content" });

        Assert.True(result.Success);
        Assert.True(File.Exists(nested));
        Assert.Equal("deep content", await File.ReadAllTextAsync(nested));
    }

    // ── ListFilesTool ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListFiles_ListsTempDir_ContainsCreatedFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "b.txt"), "b");

        var tool = new ListFilesTool(new[] { _tempRoot });
        var result = await tool.ExecuteAsync(new[] { _tempRoot });

        Assert.True(result.Success);
        Assert.Contains("a.txt", result.Output);
        Assert.Contains("b.txt", result.Output);
    }

    [Fact]
    public async Task ListFiles_WithGlobFilter_ReturnsOnlyMatches()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "data.csv"), "a,b");
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "notes.txt"), "notes");

        var tool = new ListFilesTool(new[] { _tempRoot });
        var result = await tool.ExecuteAsync(new[] { _tempRoot, "*.csv" });

        Assert.True(result.Success);
        Assert.Contains("data.csv", result.Output);
        Assert.DoesNotContain("notes.txt", result.Output);
    }

    [Fact]
    public async Task ListFiles_PathOutsideAllowedRoots_Fails()
    {
        var tool = new ListFilesTool(new[] { _tempRoot });
        var outside = Path.GetTempPath();

        var result = await tool.ExecuteAsync(new[] { outside });

        Assert.False(result.Success);
    }

    // ── RunBashTool ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RunBash_NonAllowedCommand_ReturnsFailure()
    {
        var tool = new RunBashTool();

        var result = await tool.ExecuteAsync(new[] { "rm -rf /" });

        Assert.False(result.Success);
        Assert.Contains("does not start with an allowed prefix", result.Error);
    }

    [Fact]
    public async Task RunBash_EchoCommand_CapturesStdout()
    {
        var tool = new RunBashTool(
            allowedPrefixes: new[] { "echo" },
            timeout: TimeSpan.FromSeconds(10));

        var result = await tool.ExecuteAsync(new[] { "echo hello" });

        Assert.True(result.Success, $"Expected success but got: {result.Error}");
        Assert.Contains("hello", result.Output);
    }

    [Fact]
    public async Task RunBash_MissingArgument_ReturnsFailure()
    {
        var tool = new RunBashTool();

        var result = await tool.ExecuteAsync(Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Contains("Missing required argument", result.Error);
    }
}
