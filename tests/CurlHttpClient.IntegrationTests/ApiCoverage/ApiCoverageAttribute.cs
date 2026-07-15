namespace CurlHttp.IntegrationTests.ApiCoverage;

/// <summary>
/// Declares that a test method exercises a specific public HttpClient API,
/// identified by its canonical signature from
/// <see cref="HttpClientApiInventory"/> (e.g.
/// "HttpClient.GetAsync(String, HttpCompletionOption, CancellationToken)").
/// The coverage gate cross-references these attributes against the committed
/// API baseline; a signature that matches nothing in the inventory fails the
/// gate (typo protection).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ApiCoverageAttribute(string signature) : Attribute
{
    public string Signature { get; } = signature;
}
