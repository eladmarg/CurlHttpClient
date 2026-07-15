using System.Runtime.InteropServices;

namespace CurlHttp.Native;

/// <summary>Mirror of the native <c>curl_bridge_result</c> enum.</summary>
internal enum CurlBridgeResult
{
    Ok = 0,
    Cancelled = 1,
    Timeout = 2,
    ConnectTimeout = 3,
    TlsError = 4,
    CertError = 5,
    DnsError = 6,
    ConnectError = 7,
    NetworkError = 8,
    ProtocolError = 9,
    TooManyRedirects = 10,
    CallbackError = 11,
    InvalidArgument = 12,
    Unsupported = 13,
    InternalError = 100,
}

/// <summary>Header-line flags mirroring the CURL_BRIDGE_HEADER_* constants.</summary>
[Flags]
internal enum HeaderLineFlags : uint
{
    None = 0,
    StatusLine = 0x1,
    Informational = 0x2,
    Trailer = 0x4,
    BlockEnd = 0x8,
}

/// <summary>Mirror of <c>curl_bridge_client_options</c>. All pointer fields
/// reference memory that only needs to live for the duration of the
/// curl_bridge_client_create call (the bridge deep-copies).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BridgeClientOptionsNative
{
    public uint StructSize;

    public IntPtr CaBundlePem;
    public ulong CaBundlePemLength;
    public int UseNativeCa;
    public int MinTlsVersion;
    public IntPtr Tls12CipherList;
    public IntPtr Tls13CipherSuites;
    public IntPtr ClientCertPath;
    public IntPtr ClientCertType;
    public IntPtr ClientKeyPath;
    public IntPtr ClientKeyPassword;

    public long ConnectTimeoutMs;
    public long RequestTimeoutMs;
    public int FollowRedirects;
    public int MaxRedirects;
    public int EnableDecompression;
    public int EnableHttp2;
    public int EnableCookieEngine;
    public int Verbose;
    public int BufferSize;

    public int MaxEasyHandles;
    public long ConnectionIdleTimeoutSecs;
    public long ConnectionMaxLifetimeSecs;
}

/// <summary>Mirror of <c>curl_bridge_callbacks</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BridgeCallbacksNative
{
    public uint StructSize;
    public void* Context;
    public delegate* unmanaged[Cdecl]<void*, byte*, nuint, int> OnBodyData;
    public delegate* unmanaged[Cdecl]<void*, byte*, nuint, uint, int, int> OnHeaderLine;
    public delegate* unmanaged[Cdecl]<void*, byte*, nuint, long> OnReadBody;
    public delegate* unmanaged[Cdecl]<void*, long, int, int> OnSeekBody;
    public delegate* unmanaged[Cdecl]<void*, int, byte*, nuint, void> OnDebug;
}

/// <summary>Mirror of <c>curl_bridge_response_info</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BridgeResponseInfoNative
{
    public uint StructSize;
    public int StatusCode;
    public int HttpVersion;
    public int CurlErrorCode;
    public int NumConnects;
    public int RedirectCount;
    public int Reserved0;
    public long NameLookupTimeUs;
    public long ConnectTimeUs;
    public long AppConnectTimeUs;
    public long StartTransferTimeUs;
    public long TotalTimeUs;
    public long ContentLength;

    public static BridgeResponseInfoNative Create()
    {
        return new BridgeResponseInfoNative
        {
            StructSize = (uint)Marshal.SizeOf<BridgeResponseInfoNative>(),
            ContentLength = -1,
        };
    }
}
