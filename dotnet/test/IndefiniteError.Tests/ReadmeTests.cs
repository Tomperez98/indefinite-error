using System.Diagnostics;
using System.Text.RegularExpressions;

namespace IndefiniteError.Tests;

/// <summary>The README's "Try it" is <c>examples/TryIt/TryIt.cs</c>, and prints what the README shows.</summary>
public sealed partial class ReadmeTests
{
    private static readonly string Root = Path.Combine(Spec.Directory, "..", "dotnet");

    [GeneratedRegex(@"## Try it\n.*?```csharp\n(?<code>.*?)```\n\n```text\n(?<output>.*?)```", RegexOptions.Singleline)]
    private static partial Regex TryIt();

    private static (string Code, string[] Output) Readme()
    {
        var match = TryIt().Match(File.ReadAllText(Path.Combine(Root, "README.md")).ReplaceLineEndings("\n"));
        Assert.True(match.Success, "README.md has a Try it section with a csharp block and a text block");
        return (match.Groups["code"].Value, match.Groups["output"].Value.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void The_example_in_the_readme_is_the_file() =>
        Assert.Equal(
            File.ReadAllText(Path.Combine(Root, "examples", "TryIt", "TryIt.cs")).ReplaceLineEndings("\n"),
            Readme().Code);

    [Fact]
    public async Task The_example_prints_what_the_readme_shows()
    {
        var (_, output) = Readme();
        var start = new ProcessStartInfo("dotnet", ["run", "TryIt.cs"])
        {
            WorkingDirectory = Path.Combine(Root, "examples", "TryIt"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var name in new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH" })
        {
            start.Environment.Remove(name); // this test's own build must not leak into the example's
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"dotnet run TryIt.cs exited {process.ExitCode}: {await stderr}");

        // Two pipes don't keep their relative order: check each stream's lines in order.
        static string[] Lines(string text) => text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.Equal(output.Where(l => !l.StartsWith("indefinite-error:", StringComparison.Ordinal)), Lines(await stdout));
        Assert.Equal(output.Where(l => l.StartsWith("indefinite-error:", StringComparison.Ordinal)), Lines(await stderr));
    }
}
