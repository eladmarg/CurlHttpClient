using System.Runtime.InteropServices;

namespace CurlHttp.Native;

/// <summary>Owns a native <c>curl_bridge_client*</c>. Release blocks briefly
/// until no request is actively using the client (native backstop); the
/// handler drains its worker pool before disposing this handle.</summary>
internal sealed class CurlBridgeClientHandle : SafeHandle
{
    public CurlBridgeClientHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.ClientDestroy(handle);
        return true;
    }
}

/// <summary>Owns a native <c>curl_bridge_multi_client*</c> (event-loop engine).
/// Release stops the loop thread and frees the multi handle + pool.</summary>
internal sealed class CurlBridgeMultiClientHandle : SafeHandle
{
    public CurlBridgeMultiClientHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.MultiDestroy(handle);
        return true;
    }
}

/// <summary>Owns a native <c>curl_bridge_request*</c>. The SafeHandle
/// ref-counting guarantees the native object cannot be destroyed while a
/// P/Invoke (including the blocking send and cross-thread cancel) is using it.</summary>
internal sealed class CurlBridgeRequestHandle : SafeHandle
{
    public CurlBridgeRequestHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.RequestDestroy(handle);
        return true;
    }
}
