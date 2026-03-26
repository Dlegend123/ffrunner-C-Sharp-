using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace ffrunner
{
    public partial class App : Application
    {
        public static Arguments Args = new();
        private static IntPtr _sVectoredExceptionHandler = IntPtr.Zero;
        private static VectoredExceptionHandlerDelegate? _sVectoredExceptionHandlerDelegate;
        public static IntPtr NpUnityDll = IntPtr.Zero;
        
        private const int SOk = 0;
        public static MainWindow? mainWindow;
        // StartPlugin follows the native ordering and keeps buffers alive for plugin lifetime.
        public static void SetDependencies(Arguments args)
        {
            try
            {

                // Environment setup

                // Init browser-side NPAPI structures used by plugin
                EnableDpiAwareness();
                SetUnityEnvironment(args);

                Logger.Log(
                    $"StartPlugin environment UNITY_HOME_DIR='{Environment.GetEnvironmentVariable("UNITY_HOME_DIR")}', CurrentDirectory='{Environment.CurrentDirectory}'");

                // Load plugin DLL
                NpUnityDll = LoadLibrary("npUnity3D32.dll");
                if (NpUnityDll == IntPtr.Zero)
                    throw new Exception($"Failed to load DLL: {Marshal.GetLastWin32Error()}");

            }
            catch (Exception ex)
            {
                //Logger.Log($"Plugin initialization failed: {ex}");
                MessageBox.Show($"Plugin initialization failed: {ex.Message}", "Error", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                throw;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Logger.Init(Defaults.LOG_FILE_PATH, false);
            Logger.Log("App.OnStartup entered");

            ShutdownMode = ShutdownMode.OnMainWindowClose;

            Args = ParseArgs(e.Args);
            Logger.Init(Args.LogPath, Args.VerboseLogging);

            // === Load the plugin BEFORE creating window ===
            SetDependencies(Args);
            ApplyVramFix();

            // Now create the window
            mainWindow = new MainWindow(Args);
            MainWindow = mainWindow;

            try
            {
                App.Args.AssetUrl = @"C:\Users\Mark Morrison\Desktop\OpenFusion\OpenFusionLauncher\offline_cache\6543a2bb-d154-4087-b9ee-3c8aa778580a\";
                App.Args.MainPathOrAddress = @"C:\Users\Mark Morrison\Desktop\OpenFusion\OpenFusionLauncher\offline_cache\6543a2bb-d154-4087-b9ee-3c8aa778580a\main.unity3d";
                App.Args.ServerAddress = "127.0.0.1:8023";
                App.Args.TegId = "mlegend123";
                App.Args.AuthId = "mlegend123";
                NormalizeLocalPaths(App.Args);
                // Before showing the window or initializing the plugin
                PluginMemory.InitMemory(
                    App.Args.AssetUrl,
                    App.Args.MainPathOrAddress,
                    App.Args.TegId,
                    App.Args.AuthId
                );

                // Now the plugin can read MemoryBlock directly like requests.c does
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                Logger.Log($"MainWindow.Show failed: {ex}");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
        
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int VectoredExceptionHandlerDelegate(IntPtr exceptionPointers);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        
        
        private static void ApplyVramFix()
        {
            Logger.Log("ApplyVramFix entered");
            try
            {
                ulong vramBytes = 0;
                var primaryMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);

                using var factory = CreateDXGIFactory1<IDXGIFactory1>();

                for (uint i = 0; ; i++)
                {
                    var adapterResult = factory.EnumAdapters(i, out var adapter);
                    if (adapterResult.Failure || adapter == null)
                        break;

                    using (adapter)
                    {
                        var isPrimaryAdapter = false;

                        for (uint j = 0; ; j++)
                        {
                            var outputResult = adapter.EnumOutputs(j, out var output);
                            if (outputResult.Failure || output == null)
                                break;

                            using (output)
                            {
                                var outputDesc = output.Description;
                                if (outputDesc.Monitor == primaryMonitor)
                                    isPrimaryAdapter = true;
                            }
                        }

                        if (!isPrimaryAdapter)
                            continue;

                        var adapterDesc = adapter.Description;
                        vramBytes = adapterDesc.DedicatedVideoMemory + adapterDesc.SharedSystemMemory;
                        break;
                    }
                }

                if (vramBytes == 0)
                {
                    Logger.Log("Failed to get VRAM size from DXGI; game will try to query it");
                    return;
                }

                var vramMegabytes = vramBytes >> 20;
                Logger.Log($"VRAM size: {vramBytes} bytes ({vramMegabytes} MB)");
                Environment.SetEnvironmentVariable("UNITY_FF_VRAM_MB", vramMegabytes.ToString());
                Logger.Log($"setenv(\"UNITY_FF_VRAM_MB\", \"{vramMegabytes}\")");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get VRAM size from DXGI; game will try to query it: {ex.Message}");
            }
        }

        private static void SetUnityEnvironment(Arguments parsed)
        {
            Logger.Log($"SetUnityEnvironment entered with main='{parsed.MainPathOrAddress}', assetUrl='{parsed.AssetUrl}', address='{parsed.ServerAddress}'");
            var launcherHome = SelectLauncherHomeDirectory();
            RebaseOfflineCachePaths(parsed, launcherHome);
            Environment.CurrentDirectory = launcherHome;

            Environment.SetEnvironmentVariable("UNITY_HOME_DIR", Environment.CurrentDirectory);
            Logger.Log($"setenv(\"UNITY_HOME_DIR\", \"{Environment.CurrentDirectory}\")");
            Environment.SetEnvironmentVariable("UNITY_DISABLE_PLUGIN_UPDATES", "yes");
            Environment.SetEnvironmentVariable("LANG", null);
            Environment.SetEnvironmentVariable("UNITY_KEEP_LOG_FILES", "yes");
            App.Args.ForceVulkan = true;
            if (parsed.ForceVulkan)
            {
                Environment.SetEnvironmentVariable("UNITY_FF_DX_DLL", "d3d9_vulkan.dll");
            }

            Logger.Log($"SetUnityEnvironment completed with UNITY_HOME_DIR='{Environment.CurrentDirectory}', main='{parsed.MainPathOrAddress}', assetUrl='{parsed.AssetUrl}'");

        }

        private static void RebaseOfflineCachePaths(Arguments parsed, string launcherHome)
        {
            Logger.Log($"RebaseOfflineCachePaths entered launcherHome='{launcherHome}', main='{parsed.MainPathOrAddress}', assetUrl='{parsed.AssetUrl}'");
            static string? TryGetLocalPath(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return null;

                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
                    return uri.LocalPath;

                return Path.IsPathRooted(value) ? Path.GetFullPath(value) : null;
            }

            static string? TryGetOfflineCacheRelativePath(string fullPath)
            {
                var normalized = Path.GetFullPath(fullPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var marker = Path.DirectorySeparatorChar + "offline_cache" + Path.DirectorySeparatorChar;
                var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    return null;

                return normalized.Substring(idx + 1);
            }

            static string Rebase(string? originalValue, string launcherRoot, bool expectDirectory)
            {
                var localPath = TryGetLocalPath(originalValue);
                if (string.IsNullOrWhiteSpace(localPath))
                    return originalValue ?? string.Empty;

                var relative = TryGetOfflineCacheRelativePath(localPath);
                if (string.IsNullOrWhiteSpace(relative))
                    return originalValue ?? string.Empty;

                var candidate = Path.Combine(launcherRoot, relative);
                var exists = expectDirectory ? Directory.Exists(candidate) : File.Exists(candidate);
                if (!exists)
                    return originalValue ?? string.Empty;

                var rebased = new Uri(expectDirectory && !candidate.EndsWith(Path.DirectorySeparatorChar)
                    ? candidate + Path.DirectorySeparatorChar
                    : candidate).AbsoluteUri;

                if (!string.Equals(originalValue, rebased, StringComparison.OrdinalIgnoreCase))
                    Logger.Log($"Rebased {(expectDirectory ? "asset" : "main")} path from '{originalValue}' to '{rebased}'");

                return rebased;
            }

            if (string.IsNullOrWhiteSpace(launcherHome) || !Directory.Exists(launcherHome))
                return;

            parsed.MainPathOrAddress = Rebase(parsed.MainPathOrAddress, launcherHome, expectDirectory: false);
            parsed.AssetUrl = Rebase(parsed.AssetUrl, launcherHome, expectDirectory: true);
            Logger.Log($"RebaseOfflineCachePaths completed main='{parsed.MainPathOrAddress}', assetUrl='{parsed.AssetUrl}'");
        }

        private static string SelectLauncherHomeDirectory()
        {
            Logger.Log("SelectLauncherHomeDirectory entered");
            static bool HasLoaderImages(string root)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    return false;

                var imgDir = Path.Combine(root, "assets", "img");
                return File.Exists(Path.Combine(imgDir, "unity-dexlabs.png"))
                    && File.Exists(Path.Combine(imgDir, "unity-loadingbar.png"))
                    && File.Exists(Path.Combine(imgDir, "unity-loadingframe.png"));
            }

            static string? TryGetLauncherRootFromOfflineCachePath(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return null;

                string? path = null;
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
                    path = uri.LocalPath;
                else if (Path.IsPathRooted(value))
                    path = value;

                if (string.IsNullOrWhiteSpace(path))
                    return null;

                var fullPath = Path.GetFullPath(path);
                var dir = Directory.Exists(fullPath)
                    ? new DirectoryInfo(fullPath)
                    : Directory.GetParent(fullPath);

                while (dir != null)
                {
                    if (string.Equals(dir.Name, "offline_cache", StringComparison.OrdinalIgnoreCase))
                        return dir.Parent?.FullName;

                    dir = dir.Parent;
                }

                return null;
            }

            var current = Environment.CurrentDirectory;
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var baseParent = Directory.GetParent(baseDir)?.FullName;
            var resourcesApp = Path.Combine(baseDir, "resources", "app");
            var fromMain = TryGetLauncherRootFromOfflineCachePath(Args.MainPathOrAddress);
            var fromAssets = TryGetLauncherRootFromOfflineCachePath(Args.AssetUrl);

            string[] candidates =
            {
                current,
                baseDir,
                resourcesApp,
                baseParent ?? string.Empty,
                fromMain ?? string.Empty,
                fromAssets ?? string.Empty,
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                Logger.Log($"Checking launcher home candidate '{candidate}'");
                if (HasLoaderImages(candidate))
                {
                    Logger.Log($"Using launcher home directory '{candidate}'");
                    return candidate;
                }
            }

            Logger.Log($"Loader images not found in known locations, keeping current directory '{current}'");
            return current;
        }

        public static Arguments ParseArgs(string[] args)
        {
            Logger.Log($"ParseArgs entered argc={args.Length}");
            var parsed = new Arguments();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                var option = arg;
                string? inlineValue = null;

                var equalsIndex = arg.IndexOf('=');
                if (equalsIndex > 0)
                {
                    option = arg.Substring(0, equalsIndex);
                    inlineValue = arg.Substring(equalsIndex + 1);
                }

                string? NextValue()
                {
                    if (inlineValue != null)
                        return inlineValue;

                    if (i + 1 < args.Length)
                        return args[++i];

                    return null;
                }

                switch (option)
                {
                    case "-v":
                    case "--verbose":
                        parsed.VerboseLogging = true; break;
                    case "-m":
                    case "--main":
                        parsed.MainPathOrAddress = NextValue() ?? parsed.MainPathOrAddress; break;
                    case "-l":
                    case "--log":
                        parsed.LogPath = NextValue() ?? parsed.LogPath; break;
                    case "-a":
                    case "--address":
                        parsed.ServerAddress = NextValue() ?? parsed.ServerAddress; break;
                    case "--asseturl":
                        parsed.AssetUrl = NextValue() ?? parsed.AssetUrl; break;
                    case "-e":
                    case "--endpoint":
                        parsed.EndpointHost = NextValue() ?? parsed.EndpointHost; break;
                    case "-u":
                    case "--username":
                        parsed.TegId = NextValue() ?? parsed.TegId; break;
                    case "-t":
                    case "--token":
                        parsed.AuthId = NextValue() ?? parsed.AuthId; break;
                    case "--width":
                        if (int.TryParse(NextValue(), out var w)) parsed.WindowWidth = w; break;
                    case "--height":
                        if (int.TryParse(NextValue(), out var h)) parsed.WindowHeight = h; break;
                    case "--fullscreen":
                        parsed.Fullscreen = true; break;
                    case "--loader-images":
                        parsed.UseEndpointLoadingScreen = true; break;
                    case "--force-vulkan":
                        parsed.ForceVulkan = true; break;
                    case "--force-opengl":
                        parsed.ForceOpenGl = true; break;
                    case "-h":
                    case "--help":
                        ShowHelp();
                        Environment.Exit(0); break;
                }

                Logger.Log($"ParseArgs processed option='{option}', main='{parsed.MainPathOrAddress}', assetUrl='{parsed.AssetUrl}', address='{parsed.ServerAddress}', log='{parsed.LogPath}', user='{parsed.TegId}'");
            }

            if (string.IsNullOrEmpty(parsed.MainPathOrAddress))
            {
                parsed.MainPathOrAddress = Defaults.FALLBACK_SRC_URL;
            }
            if (parsed.WindowWidth <= 0) parsed.WindowWidth = Defaults.DEFAULT_WIDTH;
            if (parsed.WindowHeight <= 0) parsed.WindowHeight = Defaults.DEFAULT_HEIGHT;

            if (string.IsNullOrEmpty(parsed.AssetUrl))
            {
                parsed.AssetUrl = Defaults.FALLBACK_ASSET_URL;
            }
            if (string.IsNullOrEmpty(parsed.ServerAddress))
            {
                parsed.ServerAddress = Defaults.FALLBACK_SERVER_ADDRESS;
            }

            NormalizeLocalPaths(parsed);

            Logger.Log($"ParseArgs completed main='{parsed.MainPathOrAddress}', assetUrl='{parsed.AssetUrl}', address='{parsed.ServerAddress}', log='{parsed.LogPath}', width={parsed.WindowWidth}, height={parsed.WindowHeight}, fullscreen={parsed.Fullscreen}");

            return parsed;
        }

        public static void NormalizeLocalPaths(Arguments parsed)
        {
            Logger.Log($"NormalizeLocalPaths entered main='{parsed.MainPathOrAddress}', assetUrl='{parsed.AssetUrl}'");
            parsed.MainPathOrAddress = NormalizePathOrUrl(parsed.MainPathOrAddress, ensureTrailingSlash: false);
            parsed.AssetUrl = NormalizePathOrUrl(parsed.AssetUrl, ensureTrailingSlash: true);
            Logger.Log($"NormalizeLocalPaths completed main='{parsed.MainPathOrAddress}', assetUrl='{parsed.AssetUrl}'");
        }

        public static string NormalizePathOrUrl(string? value, bool ensureTrailingSlash)
        {
            Logger.Log($"NormalizePathOrUrl entered value='{value}', ensureTrailingSlash={ensureTrailingSlash}");
            if (string.IsNullOrEmpty(value))
                return string.Empty;


            // If it's a rooted filesystem path, force file:///
            if (Path.IsPathRooted(value))
            {
                var full = Path.GetFullPath(value);
                if (ensureTrailingSlash && Directory.Exists(full) && !full.EndsWith(Path.DirectorySeparatorChar) &&
                    !full.EndsWith(Path.AltDirectorySeparatorChar))
                    full += Path.DirectorySeparatorChar;

                var rooted = new Uri(full).AbsoluteUri.Replace("%20", " ");
                Logger.Log($"NormalizePathOrUrl rooted -> '{rooted}'");
                
                return rooted;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                {
                    var path = uri.LocalPath;
                    if (ensureTrailingSlash && Directory.Exists(path) && !path.EndsWith(Path.DirectorySeparatorChar) &&
                        !path.EndsWith(Path.AltDirectorySeparatorChar))
                        path += Path.DirectorySeparatorChar;

                    var fileUri = new Uri(path).AbsoluteUri;
                    Logger.Log($"NormalizePathOrUrl file uri -> '{fileUri}'");
                    return fileUri;
                }

                // already a real URL (http/https/etc)
                Logger.Log($"NormalizePathOrUrl absolute uri passthrough -> '{value}'");
                return value;
            }

            value = Path.GetFullPath(value);

            Logger.Log($"NormalizePathOrUrl absolute uri passthrough -> '{value}'");
            return value;
        }

        public static void EnableDpiAwareness()
        {
            try
            {
                var shcore = LoadLibrary("loader\\SHCore.dll");
                if (shcore != IntPtr.Zero)
                {
                    var result = SetProcessDpiAwareness(ProcessDpiAwareness.PROCESS_PER_MONITOR_DPI_AWARE);

                    Logger.Log(result == SOk
                        ? "Set DPI awareness to PROCESS_PER_MONITOR_DPI_AWARE"
                        : $"Failed to set DPI awareness: {result}");
                }
                else
                {
                    // Fallback for older systems
                    SetProcessDPIAware();
                }

            }
            catch (Exception ex)
            {
               // Logger.Log($"SetProcessDpiAwareness failed (not supported on this OS?): {ex}");
            }
        }


        private static void ShowHelp()
        {
            Console.WriteLine("Usage: FFRunner.exe [OPTION...]");
            Console.WriteLine("  -m, --main=STR          The main URL to load");
            Console.WriteLine("  -l, --log=STR           The path to the log file");
            Console.WriteLine("  -v, --verbose           Enable verbose logging");
            Console.WriteLine("  -a, --address=STR       The address of the server");
            Console.WriteLine("      --asseturl=STR      The URL of the CDN for assets");
            Console.WriteLine("  -e, --endpoint=STR      The OFAPI endpoint URL");
            Console.WriteLine("  -u, --username=STR      Username for auto-login");
            Console.WriteLine("  -t, --token=STR         Password or token for auto-login");
            Console.WriteLine("      --width=INT         The width of the window");
            Console.WriteLine("      --height=INT        The height of the window");
            Console.WriteLine("      --loader-images     Use loading screen images from endpoint (flag)");
            Console.WriteLine("      --force-vulkan      Force Vulkan renderer");
            Console.WriteLine("      --force-opengl      Force OpenGL renderer");
            Console.WriteLine("      --fullscreen        Makes window borderless fullscreen");
            Console.WriteLine("  -h, --help              Display this help menu");
        }

        #region Win32 Imports
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr hModule);

        private enum ProcessDpiAwareness
        {
            PROCESS_DPI_UNAWARE = 0,
            PROCESS_SYSTEM_DPI_AWARE = 1,
            PROCESS_PER_MONITOR_DPI_AWARE = 2
        }

        [DllImport("SHCore.dll")]
        private static extern int SetProcessDpiAwareness(ProcessDpiAwareness awareness);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        #endregion
    }
}