using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Media3D;
using static ffrunner.NPAPIProcs;
using static ffrunner.NPAPIStubs;
using static ffrunner.Structs;

namespace ffrunner
{

    public static class PluginBootstrap
    {
        public static Structs.NPPluginFuncs pluginFuncs;
        public static IntPtr scriptableObject; // NPObject*

        // Persistent unmanaged netscape funcs buffer (must live for plugin lifetime)
        public static IntPtr s_hwnd = IntPtr.Zero;
        public static Structs.NPWindow s_npWindow;
        public static readonly object s_windowChangeLock = new();
        public static bool s_windowChangePending;
        public static IntPtr s_pendingWindowHwnd = IntPtr.Zero;
        public static bool s_pendingWindowMinimized;

        private const int S_OK = 0;


        // Plugin/instance state
        public static Structs.NPP_t npp;

        // Buffers for argn/argp kept alive for plugin lifetime
        public static IntPtr[] argnPtrs = Array.Empty<IntPtr>();
        public static IntPtr[] argpPtrs = Array.Empty<IntPtr>();
        public static IntPtr argnUnmanaged = IntPtr.Zero;
        public static IntPtr argpUnmanaged = IntPtr.Zero;

        // Correctly-typed delegate holders (fully-qualified nested delegate types)
        private static NPAPIProcs.NP_GetEntryPointsDelegate? NP_GetEntryPoints;
        private static NPAPIProcs.NP_InitializeDelegate? NP_Initialize;

        public static NPAPIProcs.NP_ShutdownDelegate? NP_Shutdown;
        // Expose unmanaged NPP* storage pointer so other components (Network) can use it when calling plugin callbacks.
        public static IntPtr nppUnmanagedPtr = IntPtr.Zero;
        public static IntPtr mimePtr = IntPtr.Zero;
        public static IntPtr s_savedDataPtr = IntPtr.Zero;
        public static IntPtr s_savedDataPtrPtr = IntPtr.Zero;
        public const ushort NP_VERSION_MAJOR = 27;
        public const ushort NP_VERSION_MINOR = 0;
        public const ushort NP_VERSION = (NP_VERSION_MAJOR << 8) | NP_VERSION_MINOR;

        // Helper: allocate unmanaged null-terminated UTF8 string
        public static IntPtr StringToUtf8(string str)
        {
            Logger.Verbose($"StringToUtf8 input='{str}'");
            str ??= "";
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(str + "\0");
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }


        // StartPlugin follows the native ordering and keeps buffers alive for plugin lifetime.
        public static void StartPlugin(IntPtr hwnd)
        {
            Logger.Log($"StartPlugin entered hwnd=0x{hwnd.ToString("x")}");
            try
            {
                s_hwnd = hwnd;

                // Environment setup

                Network.InitializeWindow(hwnd);

                SetBrowserWindowHandle(hwnd);
                ValidateStructSizes();
                // Resolve core NP exports (raw function pointers)
                var pNP_GetEntryPoints = GetProcAddress(App.NpUnityDll, "NP_GetEntryPoints");
                var pNP_Initialize = GetProcAddress(App.NpUnityDll, "NP_Initialize");
                var pNP_Shutdown = GetProcAddress(App.NpUnityDll, "NP_Shutdown");

                if (pNP_GetEntryPoints == IntPtr.Zero || pNP_Initialize == IntPtr.Zero || pNP_Shutdown == IntPtr.Zero)
                    throw new Exception("Missing NPAPI exports");

                NP_Initialize = Marshal.GetDelegateForFunctionPointer<NP_InitializeDelegate>(pNP_Initialize);
                NP_Initialize(ref App.NetscapeFuncs);
                NP_GetEntryPoints = Marshal.GetDelegateForFunctionPointer<NP_GetEntryPointsDelegate>(pNP_GetEntryPoints);
                NP_Shutdown = Marshal.GetDelegateForFunctionPointer<NP_ShutdownDelegate>(pNP_Shutdown);
                pluginFuncs = new NPPluginFuncs
                {
                    size = (ushort)Marshal.SizeOf<NPPluginFuncs>(),
                    version = NP_VERSION
                };
                NP_GetEntryPoints(ref pluginFuncs);

                var launcherRoot = Environment.GetEnvironmentVariable("UNITY_HOME_DIR")
                                   ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar,
                                       Path.AltDirectorySeparatorChar);
                var cacheRoot = Path.Combine(launcherRoot, "offline_cache", "6543a2bb-d154-4087-b9ee-3c8aa778580a");
                //var mainPath = Path.Combine(cacheRoot, "main.unity3d");


                App.Args.AssetUrl = @"C:\Users\Mark Morrison\Desktop\OpenFusion\OpenFusionLauncher\offline_cache\6543a2bb-d154-4087-b9ee-3c8aa778580a\";
                App.Args.MainPathOrAddress = @"C:\Users\Mark Morrison\Desktop\OpenFusion\OpenFusionLauncher\offline_cache\6543a2bb-d154-4087-b9ee-3c8aa778580a\main.unity3d";
                App.Args.ServerAddress = "127.0.0.1:8023";
                App.Args.TegId = "mlegend123";
                App.Args.AuthId = "mlegend123";
                Logger.Log($"Overriding launcher args from local cache root '{cacheRoot}'");
                 App.NormalizeLocalPaths(App.Args);

                // Allocate persistent unmanaged copy (with padding) BEFORE NP_Initialize

                // Build argn / argp
                string[] argn =
                {
                    "src", "width", "height", "bordercolor", "backgroundcolor",
                    "disableContextMenu", "textcolor", "logoimage", "progressbarimage", "progressframeimage"
                };

                Logger.Log(
                    $"Args: main='{App.Args.MainPathOrAddress}', assetUrl='{App.Args.AssetUrl}', address='{App.Args.ServerAddress}', endpoint='{App.Args.EndpointHost}', username='{App.Args.TegId}', token={(string.IsNullOrEmpty(App.Args.AuthId) ? "(empty)" : "***")}");
                string[] argp =
                {
                    App.Args.MainPathOrAddress ?? "",
                    App.Args.WindowWidth.ToString(),
                    App.Args.WindowHeight.ToString(),
                    "000000", "000000", "true", "ccffff",
                    App.NormalizePathOrUrl("assets/img/unity-dexlabs.png", true),
                    App.NormalizePathOrUrl("assets/img/unity-loadingbar.png", true),
                    App.NormalizePathOrUrl("assets/img/unity-loadingframe.png", true)
                };
                
                App.NormalizeLocalPaths(App.Args);
                InitPluginDelegates(pluginFuncs);
                Network.InitNetwork(App.Args.MainPathOrAddress ?? string.Empty);

                // Use UTF8 for NPAPI strings
                mimePtr = StringToUtf8("application/vnd.ffuwp");

                // Convert managed strings to unmanaged UTF8 pointers and keep them alive
                argnPtrs = argn.Select(StringToUtf8).ToArray();
                argpPtrs = argp.Select(StringToUtf8).ToArray();

                // Allocate pointer arrays (NULL-terminated) for native plugin
                argnUnmanaged = Marshal.AllocHGlobal(IntPtr.Size * (argnPtrs.Length + 1));
                argpUnmanaged = Marshal.AllocHGlobal(IntPtr.Size * (argpPtrs.Length + 1));
                for (var i = 0; i < argnPtrs.Length; i++)
                    Marshal.WriteIntPtr(argnUnmanaged, i * IntPtr.Size, argnPtrs[i]);
                Marshal.WriteIntPtr(argnUnmanaged, argnPtrs.Length * IntPtr.Size, IntPtr.Zero);

                for (var i = 0; i < argpPtrs.Length; i++)
                    Marshal.WriteIntPtr(argpUnmanaged, i * IntPtr.Size, argpPtrs[i]);
                Marshal.WriteIntPtr(argpUnmanaged, argpPtrs.Length * IntPtr.Size, IntPtr.Zero);

                // Allocate persistent unmanaged NPP_t storage (plugin will write instance data here)
                nppUnmanagedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Structs.NPP_t>());
                Marshal.StructureToPtr(new Structs.NPP_t(), nppUnmanagedPtr, false);

                // Saved data (ephemeral)
                var saved = new Structs.NPSavedData();
                s_savedDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Structs.NPSavedData>());
                Marshal.StructureToPtr(saved, s_savedDataPtr, false);
                s_savedDataPtrPtr = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(s_savedDataPtrPtr, s_savedDataPtr);

                // Call plugin's NPP_New using pointer-based native delegate
                var newpPtr = pluginFuncs.newp;
                if (newpPtr == IntPtr.Zero)
                    throw new Exception("pluginFuncs.newp is NULL; cannot call NPP_New.");

                // Create delegate for plugin NPP_New that returns short (NPError)
                var newpUnmanaged = Marshal.GetDelegateForFunctionPointer<NPP_New_Unmanaged_Cdecl_ShortArg>(newpPtr);
                
                // NPError is 16-bit -> use short
                var newpRet = newpUnmanaged(mimePtr, nppUnmanagedPtr, 1, (short)argn.Length, argnUnmanaged,
                    argpUnmanaged, s_savedDataPtr);

                // Fallback: try minimal args if plugin rejects full args
                if (newpRet != 0)
                {
                    Logger.Log($"NPP_New (full) returned {newpRet}, trying minimal NPP_New (argc=0, saved=NULL)");
                    try
                    {
                        var minimal = newpUnmanaged(mimePtr, nppUnmanagedPtr, 0, 0, IntPtr.Zero, IntPtr.Zero,
                            IntPtr.Zero);
                        Logger.Log($"NPP_New (minimal) returned {minimal}");
                        newpRet = minimal;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"NPP_New (minimal) threw: {ex}");
                    }
                }

                if (newpRet != 0)
                    throw new Exception($"NPP_New returned error code: {newpRet}");
                Logger.Log($"NPP_New returned {newpRet}");

                // Setup NPWindow and call setwindow using unmanaged pointer-based delegate
                MainWindow.UpdateNPWindowFromWpfWindow();

                // Get scriptable object - use short NPError result
                var getvalue =
                    Marshal.GetDelegateForFunctionPointer<NPP_GetValue_Unmanaged_Cdecl>(pluginFuncs.getvalue);

                var scriptableObjectPtr = IntPtr.Zero;
                const int NPPVpluginScriptableNPObject = 15;
                getvalue(nppUnmanagedPtr, NPPVpluginScriptableNPObject, ref scriptableObjectPtr);

                scriptableObject = scriptableObjectPtr;
                InitializeScriptableObject(scriptableObjectPtr);
            }
            catch (Exception ex)
            {
                Logger.Log($"Plugin initialization failed: {ex}");
                MessageBox.Show($"Plugin initialization failed: {ex.Message}", "Error", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                throw;
            }
        }

        private static void ValidateStructSizes()
        {
            int sizeFuncs = Marshal.SizeOf<NPPluginFuncs>();
            int sizeClass = Marshal.SizeOf<NPClass>();

            Logger.Log($"NPPluginFuncs size={sizeFuncs}, NPClass size={sizeClass}");

            // Optional: throw if mismatch with expected native sizes
            if (sizeFuncs != 52) // adjust to actual sizeof(NPPluginFuncs) in C
                throw new InvalidOperationException("NPPluginFuncs size mismatch");
        }


        #region Win32 Imports
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr hModule);


        #endregion
    }
}