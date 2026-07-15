using System.Runtime.InteropServices;

namespace CurlHttp.Native;

/// <summary>P/Invoke surface of curl_http_bridge.dll. The library is resolved
/// through <see cref="NativeLibraryLoader"/> — never from PATH or the current
/// working directory.</summary>
internal static partial class NativeMethods
{
    internal const string LibraryName = "curl_http_bridge";

    static NativeMethods()
    {
        NativeLibraryLoader.RegisterResolver();
    }

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_global_initialize")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeResult GlobalInitialize();

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_get_version_info")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nuint GetVersionInfo(Span<byte> buffer, nuint bufferLength);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_get_last_global_error")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr GetLastGlobalErrorPtr();

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_enumerate_ciphers")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nuint EnumerateCiphers(Span<byte> buffer, nuint bufferLength);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_client_create")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeClientHandle ClientCreate(in BridgeClientOptionsNative options);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_client_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ClientDestroy(IntPtr client);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_create")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeRequestHandle RequestCreate(CurlBridgeClientHandle client);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void RequestDestroy(IntPtr request);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_set_method",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeResult RequestSetMethod(CurlBridgeRequestHandle request, string method);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_set_url",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeResult RequestSetUrl(CurlBridgeRequestHandle request, string url);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_add_header",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeResult RequestAddHeader(CurlBridgeRequestHandle request, string headerLine);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_set_body")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeResult RequestSetBody(CurlBridgeRequestHandle request, long contentLength);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_set_timeout")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeResult RequestSetTimeout(CurlBridgeRequestHandle request, long timeoutMs);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_set_proxy",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeResult RequestSetProxy(
        CurlBridgeRequestHandle request, string proxy, string? userPassword);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_set_redirect_protocols",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeResult RequestSetRedirectProtocols(
        CurlBridgeRequestHandle request, string protocols);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_set_callbacks")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeResult RequestSetCallbacks(
        CurlBridgeRequestHandle request, in BridgeCallbacksNative callbacks);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_send")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial CurlBridgeResult RequestSend(
        CurlBridgeRequestHandle request, ref BridgeResponseInfoNative info);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_cancel")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void RequestCancel(CurlBridgeRequestHandle request);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_get_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr RequestGetLastErrorPtr(CurlBridgeRequestHandle request);

    [LibraryImport(LibraryName, EntryPoint = "curl_bridge_request_get_effective_url")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr RequestGetEffectiveUrlPtr(CurlBridgeRequestHandle request);

    internal static string GetLastGlobalError()
        => Marshal.PtrToStringUTF8(GetLastGlobalErrorPtr()) ?? string.Empty;

    internal static string RequestGetLastError(CurlBridgeRequestHandle request)
        => Marshal.PtrToStringUTF8(RequestGetLastErrorPtr(request)) ?? string.Empty;

    internal static string RequestGetEffectiveUrl(CurlBridgeRequestHandle request)
        => Marshal.PtrToStringUTF8(RequestGetEffectiveUrlPtr(request)) ?? string.Empty;

    private delegate nuint JsonExport(Span<byte> buffer, nuint bufferLength);

    internal static string GetVersionInfoJson()
        => ReadJson(GetVersionInfo, 4096);

    internal static string GetCipherInventoryJson()
        => ReadJson(EnumerateCiphers, 64 * 1024);

    private static string ReadJson(JsonExport nativeCall, int initialSize)
    {
        byte[] buffer = new byte[initialSize];
        nuint written = nativeCall(buffer, (nuint)buffer.Length);
        if (written == 0)
        {
            return "{}";
        }
        if (written >= (nuint)buffer.Length)
        {
            buffer = new byte[(int)written + 1];
            written = nativeCall(buffer, (nuint)buffer.Length);
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)written);
    }
}
