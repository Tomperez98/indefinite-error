using System.Text.Json;

namespace IndefiniteError.Tests;

/// <summary>The repository's <c>spec/</c>: the contract every implementation is tested against.</summary>
internal static class Spec
{
    private static readonly Lazy<string> Dir = new(Find);

    /// <summary>The <c>spec/</c> directory itself.</summary>
    public static string Directory => Dir.Value;

    /// <summary>The tab-separated rows of <c>spec/{name}</c>, without its <c>#</c> comments.</summary>
    public static IReadOnlyList<string[]> Rows(string name) =>
        [.. File.ReadAllLines(Path.Combine(Dir.Value, name), System.Text.Encoding.UTF8)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))];

    /// <summary>A site cell: a JSON string.</summary>
    public static string Site(string cell) =>
        JsonSerializer.Deserialize<string>(cell) ?? throw new InvalidDataException($"site cell {cell} is not a JSON string");

    /// <summary>Found by walking up from the test binaries: tests run from a repository checkout.</summary>
    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var spec = Path.Combine(dir.FullName, "spec");
            if (File.Exists(Path.Combine(spec, "README.md")))
            {
                return spec;
            }
        }

        throw new FileNotFoundException($"no spec/README.md above {AppContext.BaseDirectory}: tests run from a repository checkout");
    }
}
