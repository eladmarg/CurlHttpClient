using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CurlHttp.Internal;

namespace CurlHttp.Native;

/// <summary>
/// The [UnmanagedCallersOnly] trampolines libcurl calls into (via the bridge).
/// These are static function pointers — nothing here can be collected or
/// relocated by the GC, which removes the classic delegate-lifetime hazard.
/// The per-request state travels as a GCHandle in the context pointer.
///
/// Contract: never let an exception escape (it would cross two native
/// frames); every failure is recorded on the context and reported to
/// libcurl through the return value.
/// </summary>
internal static unsafe class NativeCallbacks
{
    private static CurlRequestContext FromContext(void* context)
        => (CurlRequestContext)GCHandle.FromIntPtr((IntPtr)context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int OnBodyData(void* context, byte* data, nuint length)
    {
        CurlRequestContext ctx = FromContext(context);
        try
        {
            return ctx.OnBodyData(new ReadOnlySpan<byte>(data, checked((int)length)));
        }
        catch (Exception ex)
        {
            ctx.TrySetCallbackException(ex);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int OnHeaderLine(void* context, byte* line, nuint length, uint flags, int status)
    {
        CurlRequestContext ctx = FromContext(context);
        try
        {
            return ctx.OnHeaderLine(
                new ReadOnlySpan<byte>(line, checked((int)length)),
                (HeaderLineFlags)flags,
                status);
        }
        catch (Exception ex)
        {
            ctx.TrySetCallbackException(ex);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static long OnReadBody(void* context, byte* destination, nuint length)
    {
        CurlRequestContext ctx = FromContext(context);
        try
        {
            return ctx.OnReadBody(new Span<byte>(destination, checked((int)length)));
        }
        catch (Exception ex)
        {
            ctx.TrySetCallbackException(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int OnSeekBody(void* context, long offset, int origin)
    {
        CurlRequestContext ctx = FromContext(context);
        try
        {
            return ctx.OnSeekBody(offset, origin);
        }
        catch (Exception ex)
        {
            ctx.TrySetCallbackException(ex);
            return 2; // CURL_SEEKFUNC_FAIL
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnDebug(void* context, int kind, byte* data, nuint length)
    {
        try
        {
            FromContext(context).OnDebugLine(kind, new ReadOnlySpan<byte>(data, checked((int)length)));
        }
        catch
        {
            // Diagnostics must never affect the transfer.
        }
    }

    internal static BridgeCallbacksNative Create(IntPtr contextHandle, bool hasBody, bool verbose)
    {
        return new BridgeCallbacksNative
        {
            StructSize = (uint)Marshal.SizeOf<BridgeCallbacksNative>(),
            Context = (void*)contextHandle,
            OnBodyData = &OnBodyData,
            OnHeaderLine = &OnHeaderLine,
            OnReadBody = hasBody ? &OnReadBody : null,
            OnSeekBody = hasBody ? &OnSeekBody : null,
            OnDebug = verbose ? &OnDebug : null,
        };
    }
}
