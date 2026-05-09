using System.Runtime.InteropServices;
using System.Text;
using MelonLoader.Bootstrap.Logging;

namespace MelonLoader.Bootstrap.RuntimeHandlers.Il2Cpp;

internal static partial class Dotnet
{
    private const CharSet hostfxrCharSet =
#if WINDOWS
        CharSet.Unicode;
#else
        CharSet.Ansi;
#endif
    private const StringMarshalling hostfxrStringMarsh =
#if WINDOWS
        StringMarshalling.Utf16;
#else
        StringMarshalling.Utf8;
#endif

    private static nint _module;
    
    private const string rootKey = "DOTNET_ROOT";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = hostfxrCharSet)]
    private delegate int hostfxr_initialize_for_runtime_config_Fn(string runtimeConfigPath, nint parameters, ref nint hostContextHandle);
    private static hostfxr_initialize_for_runtime_config_Fn? hostfxr_initialize_for_runtime_config;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int hostfxr_get_runtime_delegate_Fn(nint context, HostfxrDelegateType type, ref LoadAssemblyAndGetFunctionPointerFn? del);
    private static hostfxr_get_runtime_delegate_Fn? hostfxr_get_runtime_delegate;

    public static bool GetHostFxrSystemPath(out string? path)
    {
        path = LoaderConfig.Current.Loader.HostFXRPathOverride;

        if (string.IsNullOrEmpty(path)
            || string.IsNullOrWhiteSpace(path))
            path = GetHostfxrPathFromExport();

        if (string.IsNullOrEmpty(path)
            || string.IsNullOrWhiteSpace(path))
            return false;

        return true;
    }

    public static bool LoadHostfxrFromFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        
        MelonDebug.Log($"HostFXR Path: {path}");

        if (!NativeLibrary.TryLoad(path, out _module))
            return false;

        nint hostfxr_initialize_for_runtime_config_ptr = NativeLibrary.GetExport(_module, nameof(hostfxr_initialize_for_runtime_config));
        if (hostfxr_initialize_for_runtime_config_ptr == nint.Zero)
            return false;
        hostfxr_initialize_for_runtime_config = Marshal.GetDelegateForFunctionPointer<hostfxr_initialize_for_runtime_config_Fn>(hostfxr_initialize_for_runtime_config_ptr);

        nint hostfxr_get_runtime_delegate_ptr = NativeLibrary.GetExport(_module, nameof(hostfxr_get_runtime_delegate));
        if (hostfxr_get_runtime_delegate_ptr == nint.Zero)
            return false;
        hostfxr_get_runtime_delegate = Marshal.GetDelegateForFunctionPointer<hostfxr_get_runtime_delegate_Fn>(hostfxr_get_runtime_delegate_ptr);

        return true;
    }
    
    public static bool TryHostFxrFromPortableDir()
    {
        string pathKey = "PATH";
        string? hostfxrPath = null;
        string? portableDir = null;
        try
        {
            portableDir = Path.Combine(Core.GameDir, "dotnet");
            if (!Directory.Exists(portableDir))
                portableDir = Path.Combine(Core.GameDir, "MelonLoader", "Dependencies", "dotnet");
            if (!Directory.Exists(portableDir))
                return false;
            if (!FindHostFxrInDirectory(portableDir, out hostfxrPath))
                return false;
        }
        catch (Exception ex)
        {
            if (LoaderConfig.Current.Loader.DebugMode)
                MelonLogger.LogError(ex.ToString());
            return false;
        }

        string? originalRoot = null;
        string? originalPath = null;
        try
        {
            // Get Original Root and Path
            originalRoot = Environment.GetEnvironmentVariable(rootKey);
            originalPath = Environment.GetEnvironmentVariable(pathKey);
        }
        catch (Exception ex)
        {
            if (LoaderConfig.Current.Loader.DebugMode)
                MelonLogger.LogError(ex.ToString());
            return false;
        }

        try
        {
            // Temporarily Apply New Root
            Environment.SetEnvironmentVariable(rootKey, portableDir);

            // Temporarily Apply New Path
            string path = originalPath ?? string.Empty;
            var pathEntries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (!pathEntries.Any(p => string.Equals(p, portableDir, StringComparison.OrdinalIgnoreCase)))
                Environment.SetEnvironmentVariable(pathKey, portableDir + Path.PathSeparator + path);
            
            // Attempt to load HostFXR
            if (LoadHostfxrFromFile(hostfxrPath))
            {
                Core.Logger.Msg($"Using portable .NET runtime: '{portableDir}'");
                return true;
            }
        }
        catch (Exception ex)
        {
            if (LoaderConfig.Current.Loader.DebugMode)
                MelonLogger.LogError(ex.ToString());
        }
        
        // Restore Original Root and Path
        try
        {
            Environment.SetEnvironmentVariable(rootKey, originalRoot);
            Environment.SetEnvironmentVariable(pathKey, originalPath);
        }
        catch (Exception ex)
        {
            if (LoaderConfig.Current.Loader.DebugMode)
                MelonLogger.LogError(ex.ToString());
        }

        return false;
    }

    private static string? GetHostfxrPathFromExport()
    {
        var buffer = new StringBuilder(1024);
        var bufferSize = (nint)buffer.Capacity;
        var result = get_hostfxr_path(buffer, ref bufferSize, 0);
        return result != 0 ? null : buffer.ToString();
    }
    
    private static bool GetHostfxrPathFromEnvironment(out string? hostfxrPath)
    {
        string? rootPath = Environment.GetEnvironmentVariable(rootKey);
        return FindHostFxrInDirectory(rootPath, out hostfxrPath);
    }

    private static bool FindHostFxrInDirectory(string? path, out string? hostfxrPath)
    {
        hostfxrPath = null;
        if (string.IsNullOrEmpty(path))
            return false;
        
        string filename = $"hostfxr.{Core.LibExtension}";
#if !WINDOWS
        filename = $"lib{filename}";
#endif

        string[] foundFiles = Directory.GetFiles(path, filename, SearchOption.AllDirectories);
        hostfxrPath = foundFiles.FirstOrDefault();
        return (hostfxrPath != null);
    }

    public static bool InitializeForRuntimeConfig(string runtimeConfigPath, out nint context)
    {
        if (hostfxr_initialize_for_runtime_config == null)
        {
            context = 0;
            return false;
        }

        nint ctx = 0;
        ConsoleHandler.NullHandles(); // Prevent it from logging its own stuff
        var status = hostfxr_initialize_for_runtime_config(runtimeConfigPath, 0, ref ctx);
        ConsoleHandler.ResetHandles();

        if (status != 0)
        {
            context = status;
            return false;
        }

        context = ctx;
        return true;
    }

    public static TDelegate? LoadAssemblyAndGetFunctionUco<TDelegate>(nint context, string assemblyPath, string typeName, string methodName) where TDelegate : Delegate
    {
        if (hostfxr_get_runtime_delegate == null)
            return null;

        LoadAssemblyAndGetFunctionPointerFn? loadAssemblyAndGetFunctionPointer = null;
        hostfxr_get_runtime_delegate(context, HostfxrDelegateType.HdtLoadAssemblyAndGetFunctionPointer, ref loadAssemblyAndGetFunctionPointer);
        if (loadAssemblyAndGetFunctionPointer == null)
            return null;

        nint funcPtr = 0;
        loadAssemblyAndGetFunctionPointer(assemblyPath, typeName, methodName, -1, 0, ref funcPtr);
        if (funcPtr == 0)
            return null;

        return Marshal.GetDelegateForFunctionPointer<TDelegate>(funcPtr);
    }

    [DllImport("*", CharSet = hostfxrCharSet)]
    private static extern int get_hostfxr_path(StringBuilder buffer, ref nint bufferSize, nint parameters);

    private enum HostfxrDelegateType
    {
        HdtComActivation,
        HdtLoadInMemoryAssembly,
        HdtWinrtActivation,
        HdtComRegister,
        HdtComUnregister,
        HdtLoadAssemblyAndGetFunctionPointer,
        HdtGetFunctionPointer,
        HdtLoadAssembly,
        HdtLoadAssemblyBytes,
    };

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = hostfxrCharSet)]
    private delegate void LoadAssemblyAndGetFunctionPointerFn(string assemblyPath, string typeName, string methodName, nint delegateTypeName, nint reserved, ref nint funcPtr);
}
