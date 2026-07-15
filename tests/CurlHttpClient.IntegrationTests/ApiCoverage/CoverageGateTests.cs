using System.Reflection;
using System.Text;
using System.Text.Json;
using CurlHttp.IntegrationTests.Infrastructure;
using Xunit;

namespace CurlHttp.IntegrationTests.ApiCoverage;

/// <summary>
/// The API-coverage gate:
///  1. The runtime HttpClient surface must EXACTLY match the committed
///     baseline (new API without classification fails; disappeared API fails).
///  2. Every [ApiCoverage] attribute must reference a real baseline entry
///     (typo protection) — enforcing.
///  3. Every baseline entry classified "Direct" must be referenced by at
///     least one executable test — report-only until the matrices land,
///     enforced when CURLHTTP_ENFORCE_COVERAGE=1 (flipped on in run-coverage
///     scripts/CI).
/// Always emits artifacts/compatibility/httpclient-api-coverage.md.
/// </summary>
public class CoverageGateTests
{
    private sealed record BaselineEntry(string Signature, string Category, string Note);

    private static (string Sdk, List<BaselineEntry> Entries) LoadBaseline()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "ApiCoverage", "httpclient-api-baseline.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var entries = doc.RootElement.GetProperty("apis").EnumerateArray()
            .Select(e => new BaselineEntry(
                e.GetProperty("signature").GetString()!,
                e.GetProperty("category").GetString()!,
                e.GetProperty("note").GetString() ?? string.Empty))
            .ToList();
        return (doc.RootElement.GetProperty("sdk").GetString()!, entries);
    }

    private static HashSet<string> CollectCoverageAttributes()
    {
        return Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(m => m.GetCustomAttributes<ApiCoverageAttribute>())
            .Select(a => a.Signature)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void RuntimeSurface_ExactlyMatchesCommittedBaseline()
    {
        IReadOnlyList<string> runtime = HttpClientApiInventory.EnumerateCurrentSurface();
        (_, List<BaselineEntry> baseline) = LoadBaseline();
        var baselineSet = baseline.Select(b => b.Signature).ToHashSet(StringComparer.Ordinal);

        var newApis = runtime.Where(s => !baselineSet.Contains(s)).ToList();
        var vanished = baselineSet.Except(runtime, StringComparer.Ordinal).ToList();

        Assert.True(newApis.Count == 0,
            "NEW HttpClient APIs appeared on this runtime without a baseline classification " +
            "(add + classify them in httpclient-api-baseline.json):\n  " + string.Join("\n  ", newApis));
        Assert.True(vanished.Count == 0,
            "Baseline APIs no longer exist on this runtime (SDK drift? see global.json):\n  " +
            string.Join("\n  ", vanished));
    }

    [Fact]
    public void EveryCoverageAttribute_ReferencesARealApi()
    {
        (_, List<BaselineEntry> baseline) = LoadBaseline();
        var known = baseline.Select(b => b.Signature).ToHashSet(StringComparer.Ordinal);
        var unknown = CollectCoverageAttributes().Where(s => !known.Contains(s)).ToList();

        Assert.True(unknown.Count == 0,
            "[ApiCoverage] attributes reference signatures that are not in the baseline (typos?):\n  " +
            string.Join("\n  ", unknown));
    }

    [Fact]
    public void EveryDirectApi_HasAnExecutableTest_AndReportIsPublished()
    {
        (string sdk, List<BaselineEntry> baseline) = LoadBaseline();
        HashSet<string> covered = CollectCoverageAttributes();

        var uncoveredDirect = baseline
            .Where(b => b.Category == "Direct" && !covered.Contains(b.Signature))
            .Select(b => b.Signature)
            .ToList();

        // Human-readable coverage report, always published.
        var md = new StringBuilder()
            .AppendLine("# HttpClient API coverage")
            .AppendLine()
            .AppendLine($"SDK: {sdk} — {baseline.Count} public APIs " +
                        $"({baseline.Count(b => b.Category == "Direct")} Direct, " +
                        $"{baseline.Count(b => b.Category == "CoveredVia")} CoveredVia, " +
                        $"{baseline.Count(b => b.Category == "NotApplicable")} NotApplicable); " +
                        $"{uncoveredDirect.Count} Direct entries still lacking a test.")
            .AppendLine()
            .AppendLine("| API | Category | Tested | Note |")
            .AppendLine("| --- | --- | --- | --- |");
        foreach (BaselineEntry entry in baseline)
        {
            string tested = entry.Category switch
            {
                "Direct" => covered.Contains(entry.Signature) ? "✅" : "❌ missing",
                "CoveredVia" => "via family",
                _ => "n/a",
            };
            md.AppendLine($"| `{entry.Signature}` | {entry.Category} | {tested} | {entry.Note} |");
        }
        ArtifactsWriter.WriteText("compatibility/httpclient-api-coverage.md", md.ToString());
        ArtifactsWriter.WriteJson("compatibility/httpclient-api-inventory.json", new
        {
            sdk,
            generatedUtc = DateTime.UtcNow,
            apis = baseline.Select(b => new
            {
                b.Signature,
                b.Category,
                Tested = b.Category == "Direct" && covered.Contains(b.Signature),
                b.Note,
            }),
        });

        bool enforce = Environment.GetEnvironmentVariable("CURLHTTP_ENFORCE_COVERAGE") == "1";
        if (enforce)
        {
            Assert.True(uncoveredDirect.Count == 0,
                "Direct-classified APIs without an [ApiCoverage]-tagged test:\n  " +
                string.Join("\n  ", uncoveredDirect));
        }
    }
}
