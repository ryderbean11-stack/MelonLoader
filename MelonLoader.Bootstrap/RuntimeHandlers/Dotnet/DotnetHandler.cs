using System.Runtime.InteropServices;
using System.Text;
using MelonLoader.Bootstrap.Logging;

namespace MelonLoader.Bootstrap.RuntimeHandlers.Dotnet;

internal static partial class DotnetHandler
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

    // Prevent GC
    private static nint _module;
    private static Action? startFunc;
    
    private const string rootKey = "DOTNET_ROOT";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = hostfxrCharSet)]
    private delegate int hostfxr_initialize_for_runtime_config_Fn(string runtimeConfigPath, nint parameters, ref nint hostContextHandle);
    private static hostfxr_initialize_for_runtime_config_Fn? hostfxr_initialize_for_runtime_config;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int hostfxr_get_runtime_delegate_Fn(nint context, HostfxrDelegateType type, ref LoadAssemblyAndGetFunctionPointerFn? del);
    private static hostfxr_get_runtime_delegate_Fn? hostfxr_get_runtime_delegate;
    
    public static void Initialize()
    {
        var managedDir = Path.Combine(LoaderConfig.Current.Loader.BaseDirectory, "MelonLoader", "net6");
        var runtimeConfigPath = Path.Combine(managedDir, "MelonLoader.runtimeconfig.json");
        var nativeHostPath = Path.Combine(managedDir, "MelonLoader.NativeHost.dll");

        if (!File.Exists(runtimeConfigPath))
        {
            Core.Logger.Error($"Runtime config not found at: '{runtimeConfigPath}'");
            return;
        }

        if (!File.Exists(nativeHostPath))
        {
            Core.Logger.Error($"NativeHost not found at: '{runtimeConfigPath}'");
            return;
        }
        
        // Try to use a portable runtime from config
        string portableDir = LoaderConfig.Current.Loader.HostFXRPathOverride;
        if (!string.IsNullOrEmpty(portableDir)
            && !string.IsNullOrWhiteSpace(portableDir))
        {
            if (File.Exists(portableDir))
                portableDir = Path.GetDirectoryName( // dotnet
                    Path.GetDirectoryName( // host
                        Path.GetDirectoryName( // fxr
                            Path.GetDirectoryName( // 6.x.x
                                portableDir))))!;

            MelonDebug.Log($"Attempting to load hostfxr using .NET runtime from: {portableDir}");
            if (ScanDirectory(portableDir)
                && InitializeDomain(runtimeConfigPath, nativeHostPath))
                return;
        }

        // Try to use a portable runtime from game directory
        portableDir = Path.Combine(Exports.ProcessDirectory, "dotnet");
        MelonDebug.Log($"Attempting to load hostfxr using .NET runtime from: {portableDir}");
        if (ScanDirectory(portableDir)
            && InitializeDomain(runtimeConfigPath, nativeHostPath))
            return;
        
        // Try to use a portable runtime from repository
        portableDir = Path.Combine(Exports.ProcessDirectory, "MelonLoader", "Dependencies", "dotnet");
        MelonDebug.Log($"Attempting to load hostfxr using .NET runtime from: {portableDir}");
        if (ScanDirectory(portableDir)
            && InitializeDomain(runtimeConfigPath, nativeHostPath))
            return;

        // Try the normal system detection/installation
        MelonDebug.Log("Attempting to load hostfxr from system");
        if (GetHostFxrSystemPath(out var path)
            && !string.IsNullOrEmpty(path)
            && LoadHostfxrFromFile(path))
        {
            Core.Logger.Msg($"Using .NET runtime: '{path}'");
            if (!InitializeDomain(runtimeConfigPath, nativeHostPath))
                return;
        }

#if WINDOWS
        // Try to install runtime then attempt system detection/installation again
        MelonDebug.Log("Attempting to reinstall .NET runtime and load hostfxr from system");
        if (DotnetInstaller.AttemptInstall()
            && GetHostFxrSystemPath(out path)
            && !string.IsNullOrEmpty(path)
            && LoadHostfxrFromFile(path))
        {
            Core.Logger.Msg($"Using .NET runtime: '{path}'");
            if (!InitializeDomain(runtimeConfigPath, nativeHostPath))
                return;
        }
#endif
        
#if X64 && (WINDOWS || OSX || LINUX)
        // Try to download portable runtime from repository then attempt to use it again
        MelonDebug.Log($"Attempting to download .NET runtime from repository and load hostfxr from: {portableDir}");
        if (DotnetPortable.AttemptInstall()
            && ScanDirectory(portableDir)
            && InitializeDomain(runtimeConfigPath, nativeHostPath))
            return;
#endif
        
        // Failure
        Core.Logger.Error("Failed to load Hostfxr");
    }

    private static bool InitializeDomain(string runtimeConfigPath, string nativeHostPath)
    {
        MelonDebug.Log("Initializing domain");
        if (!InitializeForRuntimeConfig(runtimeConfigPath, out var context))
        {
            Core.Logger.Error($"Failed to initialize a .NET domain");
            return false;
        }

        MelonDebug.Log("Loading NativeHost assembly");
        var initialize = LoadAssemblyAndGetFunctionUco<InitializeFn>(context, nativeHostPath, "MelonLoader.NativeHost.NativeEntryPoint, MelonLoader.NativeHost", "NativeEntry");
        if (initialize == null)
        {
            Core.Logger.Error($"Failed to load assembly from: '{nativeHostPath}'");
            return false;
        }

        var startFuncPtr = Exports.LibraryHandle;

        MelonDebug.Log("Invoking NativeHost entry");
        initialize(ref startFuncPtr);

        if (startFuncPtr == 0 || startFuncPtr == Exports.LibraryHandle)
        {
            Core.Logger.Error($"Managed did not return the initial function pointer");
            return false;
        }

        startFunc = Marshal.GetDelegateForFunctionPointer<Action>(startFuncPtr);
        return true;
    }
    
    public static void Start()
    {
        startFunc?.Invoke();
    }

    public static bool GetHostFxrSystemPath(out string? path)
    {
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

        MelonDebug.Log($"Attempting to use HostFXR Path: {path}");
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
        
        Core.Logger.Msg($"Using HostFXR Path: {path}");
        return true;
    }
    
    public static bool ScanDirectory(string dotnetPath)
    {
        string pathKey = "PATH";
        string? hostfxrPath = null;
        try
        {
            if (!Directory.Exists(dotnetPath))
                return false;
            if (!FindHostFxrInDirectory(dotnetPath, out hostfxrPath))
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
            Environment.SetEnvironmentVariable(rootKey, dotnetPath);

            // Temporarily Apply New Path
            string path = originalPath ?? string.Empty;
            var pathEntries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (!pathEntries.Any(p => string.Equals(p, dotnetPath, StringComparison.OrdinalIgnoreCase)))
                Environment.SetEnvironmentVariable(pathKey, dotnetPath + Path.PathSeparator + path);
            
            // Attempt to load HostFXR
            if (LoadHostfxrFromFile(hostfxrPath))
            {
                Core.Logger.Msg($"Using .NET runtime: '{dotnetPath}'");
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
        
        string filename = $"hostfxr.{Exports.LibExtension}";
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
    
    // Requires the bootstrap handle to be passed first
    private delegate void InitializeFn(ref nint startFunc);
}
