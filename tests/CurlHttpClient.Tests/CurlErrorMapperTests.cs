using System.Security.Authentication;
using CurlHttp.Internal;
using CurlHttp.Native;
using Xunit;

namespace CurlHttp.Tests;

public class CurlErrorMapperTests
{
    private static Exception Map(CurlBridgeResult result,
        CancellationToken? callerToken = null, bool streamDisposed = false,
        Exception? callbackException = null, string error = "detail")
        => CurlErrorMapper.Map(result, 7, error, callbackException, callerToken,
            timedOut: false, streamDisposed: streamDisposed);

    [Fact]
    public void CallerCancellation_CarriesTheCallerToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = Map(CurlBridgeResult.Cancelled, cts.Token);
        var tce = Assert.IsType<TaskCanceledException>(ex);
        Assert.Equal(cts.Token, tce.CancellationToken);
    }

    [Fact]
    public void StreamDisposal_MapsToObjectDisposed()
    {
        Assert.IsType<ObjectDisposedException>(
            Map(CurlBridgeResult.Cancelled, streamDisposed: true));
    }

    [Fact]
    public void RequestTimeout_IsTaskCanceledWithTimeoutInner()
    {
        var ex = Map(CurlBridgeResult.Timeout);
        var tce = Assert.IsType<TaskCanceledException>(ex);
        Assert.IsType<TimeoutException>(tce.InnerException);
    }

    [Fact]
    public void ConnectTimeout_IsHttpRequestExceptionWithTimeoutInner()
    {
        var ex = Assert.IsType<HttpRequestException>(Map(CurlBridgeResult.ConnectTimeout));
        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public void TlsFailures_MapToSecureConnectionError()
    {
        foreach (CurlBridgeResult result in
                 new[] { CurlBridgeResult.CertError, CurlBridgeResult.TlsError })
        {
            var ex = Assert.IsType<HttpRequestException>(Map(result));
            Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
            Assert.IsType<AuthenticationException>(ex.InnerException);
        }
    }

    [Fact]
    public void DnsFailure_MapsToNameResolutionError()
    {
        var ex = Assert.IsType<HttpRequestException>(Map(CurlBridgeResult.DnsError));
        Assert.Equal(HttpRequestError.NameResolutionError, ex.HttpRequestError);
    }

    [Fact]
    public void CallbackError_PrefersTheOriginalCallbackException()
    {
        var original = new HttpRequestException("stream broke");
        Assert.Same(original, Map(CurlBridgeResult.CallbackError, callbackException: original));
    }

    [Fact]
    public void NativeDetail_IsPreservedInMessageAndData()
    {
        var ex = Map(CurlBridgeResult.ProtocolError, error: "libcurl error 8 (weird reply)");
        Assert.Contains("weird reply", ex.Message);
        Assert.Equal(7, ex.Data["CurlErrorCode"]);
        Assert.Equal("ProtocolError", ex.Data["CurlBridgeResult"]);
    }
}
