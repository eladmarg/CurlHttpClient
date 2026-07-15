using Xunit;

namespace CurlHttp.IntegrationTests.Infrastructure;

/// <summary>
/// Environment gates for expensive suites. Gated tests use [SkippableFact] /
/// [SkippableTheory] and call a Require* method first, so a default run
/// reports them as SKIPPED (visible in results) — never silently green.
///
///   CURLHTTP_STRESS=1        enables stress / soak / large-payload / memory suites
///   CURLHTTP_SOAK_SECONDS=n  soak duration (default 30 when stress enabled)
///   CURLHTTP_STRESS_MB=n     configurable large-payload size (default 100)
/// </summary>
internal static class TestGate
{
    public static bool StressEnabled =>
        Environment.GetEnvironmentVariable("CURLHTTP_STRESS") == "1";

    public static void RequireStress()
        => Skip.IfNot(StressEnabled, "stress suite disabled; set CURLHTTP_STRESS=1");

    public static int SoakSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("CURLHTTP_SOAK_SECONDS"), out int s) && s > 0
            ? s
            : 30;

    public static int StressPayloadMegabytes =>
        int.TryParse(Environment.GetEnvironmentVariable("CURLHTTP_STRESS_MB"), out int mb) && mb > 0
            ? mb
            : 100;
}
