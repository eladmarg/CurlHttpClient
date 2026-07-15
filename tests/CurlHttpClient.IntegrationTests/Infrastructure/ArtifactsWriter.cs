using System.Text;
using System.Text.Json;

namespace CurlHttp.IntegrationTests.Infrastructure;

/// <summary>Writes machine-readable test artifacts under the repo's
/// artifacts/ tree (override root with CURLHTTP_ARTIFACTS).</summary>
internal static class ArtifactsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string Root { get; } = Resolve();

    private static string Resolve()
    {
        string? overridden = Environment.GetEnvironmentVariable("CURLHTTP_ARTIFACTS");
        if (!string.IsNullOrEmpty(overridden))
        {
            return Path.GetFullPath(overridden);
        }
        // Walk up from the test assembly to the repo root (contains .git or
        // the solution file), then artifacts/.
        string? dir = AppContext.BaseDirectory;
        while (dir is not null &&
               !File.Exists(Path.Combine(dir, "CurlHttpClient.slnx")) &&
               !Directory.Exists(Path.Combine(dir, ".git")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts");
    }

    public static string WriteJson<T>(string relativePath, T value)
    {
        string path = Prepare(relativePath);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
        return path;
    }

    public static string WriteText(string relativePath, string content)
    {
        string path = Prepare(relativePath);
        File.WriteAllText(path, content);
        return path;
    }

    public static string WriteCsv(string relativePath, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        foreach (string[] row in rows)
        {
            sb.AppendLine(string.Join(',', row.Select(EscapeCsv)));
        }
        return WriteText(relativePath, sb.ToString());
    }

    private static string EscapeCsv(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static string Prepare(string relativePath)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
