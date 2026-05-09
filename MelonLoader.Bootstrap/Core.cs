using System.Diagnostics;
using MelonLoader.Logging;
using MelonLoader.Bootstrap.RuntimeHandlers.Il2Cpp;
using MelonLoader.Bootstrap.RuntimeHandlers.Mono;
using MelonLoader.Bootstrap.Utils;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using MelonLoader.Bootstrap.Logging;
using Tomlet;

namespace MelonLoader.Bootstrap;

public static class Core
{
    public static nint LibraryHandle { get; private set; }

    internal static InternalLogger Logger { get; private set; } = new(ColorARGB.BlueViolet, "MelonLoader.Bootstrap");
    internal static InternalLogger PlayerLogger { get; private set; } = new(ColorARGB.Turquoise, "UNITY");
    public static string DataDir { get; private set; } = null!;
    public static string GameDir { get; private set; } = null!;
    public static string ProcessPath { get; private set; } = null!;
    
    public const string LibExtension =
#if OSX
        "dylib";
#elif WINDOWS
        "dll";
#else
        "so";
#endif

    [RequiresDynamicCode("Calls InitConfig")]
    public static void Init(nint moduleHandle)
    {
        LibraryHandle = moduleHandle;

        ProcessPath = Environment.ProcessPath!;
        GameDir = Path.GetDirectoryName(ProcessPath)!;

#if !OSX
        DataDir = Path.Combine(GameDir, Path.GetFileNameWithoutExtension(ProcessPath) + "_Data");
#else
        DataDir = Path.Combine(Path.GetDirectoryName(GameDir)!, "Resources", "Data");
#endif
        if (!Directory.Exists(DataDir))
            return;

        LoaderConfig.Initialize();

        if (LoaderConfig.Current.Loader.Disable)
            return;

        MelonLogger.Init();
        if (!LoaderConfig.Current.Loader.CapturePlayerLogs)
            ConsoleHandler.NullHandles();

        ModuleSymbolRedirect.Attach();
    }
}
