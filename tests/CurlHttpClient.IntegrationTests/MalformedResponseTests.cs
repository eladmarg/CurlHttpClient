using Xunit;

namespace CurlHttp.IntegrationTests;

[Collection("integration")]
public class MalformedResponseTests(ServerFixture fixture)
{
    [Fact]
    public async Task TruncatedBody_SurfacesAsIOExceptionWhileReading()
    {
        using var server = RawTcpServer.TruncatedBody();
        using HttpResponseMessage response = await fixture.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, server.BaseUri),
            HttpCompletionOption.ResponseHeadersRead);

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            byte[] buffer = new byte[4096];
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        });
    }

    [Fact]
    public async Task MalformedStatusLine_ThrowsHttpRequestException()
    {
        using var server = RawTcpServer.MalformedStatusLine();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.GetAsync(server.BaseUri));
    }

    [Fact]
    public async Task ConnectionClosedBeforeResponse_ThrowsHttpRequestException()
    {
        using var server = RawTcpServer.CloseBeforeResponse();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.GetAsync(server.BaseUri));
    }
}
