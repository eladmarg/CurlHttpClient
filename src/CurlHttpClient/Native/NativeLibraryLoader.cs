using System.Reflection;
using System.Runtime.InteropServices;

namespace CurlHttp.Native;

/// <summary>
/// Controlled resolution of curl_http_bridge.dll.
///
/// Security posture: the library is only ever loaded from an absolute path —
/// either one explicitly configured through
/// <see cref="CurlHttpClientOptions.NativeLibraryPath"/> or a well-known
/// location relative to this assembly. The OS default search order (current
/// directory, PATH) is never used, so a planted DLL cannot be picked up.
/// Once loaded, the module stays loaded for the process lifetime.
/// </summary>
internal static class NativeLibraryLoader
{
    private static readonly Lock Sync = new();
    private static string? _configuredPath;
    private static string? _loadedPath;
    private static IntPtr _module;
    private static bool _resolverRegistered;

    /// <summary>Full path the module was actually loaded from (diagnostics).</summary>
    internal static string? LoadedPath => _loadedPath;

    internal static void RegisterResolver()
    {
        lock (Sync)
        {
            if (_resolverRegistered)
            {
                return;
            }
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
            _resolverRegistered = true;
        }
    }

    /// <summary>Called by the handler before the first P/Invoke. Validates
    /// platform/architecture and pins the configured path. A second handler
    /// configuring a different explicit path after the library is loaded is
    /// an error (one native module per process).</summary>
    internal static void Configure(string? explicitPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "CurlHttpMessageHandler ships a Windows-only native bridge (curl_http_bridge.dll). " +
                "On other platforms use SocketsHttpHandler, which already provides OpenSSL-backed TLS.");
        }
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                $"curl_http_bridge.dll is built for x64, but this process is " +
                $"{RuntimeInformation.ProcessArchitecture}. Run the application as x64 " +
                "or rebuild the native bridge for this architecture.");
        }

        lock (Sync)
        {
            string? normalized = explicitPath is null ? null : Path.GetFullPath(explicitPath);
            if (normalized is not null && _loadedPath is not null &&
                !string.Equals(normalized, _loadedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"curl_http_bridge.dll is already loaded from '{_loadedPath}'; " +
                    $"it cannot be reloaded from '{normalized}'. The native library path " +
                    "is process-wide and must be configured before the first handler is created.");
            }
            _configuredPath ??= normalized;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeMethods.LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        lock (Sync)
        {
            if (_module != IntPtr.Zero)
            {
                return _module;
            }

            string fileName = NativeMethods.LibraryName + ".dll";
            var candidates = new List<string>();
            if (_configuredPath is not null)
            {
                candidates.Add(Directory.Exists(_configuredPath)
                    ? Path.Combine(_configuredPath, fileName)
                    : _configuredPath);
            }
            else
            {
                string assemblyDir =
                    Path.GetDirectoryName(typeof(NativeLibraryLoader).Assembly.Location) is { Length: > 0 } dir
                        ? dir
                        : AppContext.BaseDirectory;
                candidates.Add(Path.Combine(assemblyDir, "runtimes", "win-x64", "native", fileName));
                candidates.Add(Path.Combine(assemblyDir, fileName));
                if (!string.Equals(assemblyDir, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", fileName));
                    candidates.Add(Path.Combine(AppContext.BaseDirectory, fileName));
                }
            }

            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }
                // Absolute path load: no PATH / current-directory search.
                _module = NativeLibrary.Load(candidate);
                _loadedPath = candidate;
                return _module;
            }

            throw new DllNotFoundException(
                $"{fileName} was not found. Looked in: {string.Join("; ", candidates)}. " +
                "Deploy the native bridge beside the application " +
                @"(runtimes\win-x64\native\) or set CurlHttpClientOptions.NativeLibraryPath.");
        }
    }
}
