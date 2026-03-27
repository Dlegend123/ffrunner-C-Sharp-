using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using static ffrunner.NPAPIProcs;
using static ffrunner.NPAPIStubs;
using static ffrunner.Structs;

namespace ffrunner
{
    public static class PluginBootstrap
    {
        public static NPPluginFuncs pluginFuncs;
        public static IntPtr scriptableObject; // NPObject*

        public static IntPtr s_hwnd = IntPtr.Zero;
        public static NPWindow s_npWindow;
        public static readonly object s_windowChangeLock = new();
        public static bool s_windowChangePending;
        public static IntPtr s_pendingWindowHwnd = IntPtr.Zero;
        public static bool s_pendingWindowMinimized;

        public static NPP_t npp;

        public static IntPtr[] argnPtrs = Array.Empty<IntPtr>();
        public static IntPtr[] argpPtrs = Array.Empty<IntPtr>();
        public static IntPtr argnUnmanaged = IntPtr.Zero;
        public static IntPtr argpUnmanaged = IntPtr.Zero;

        private static NPAPIProcs.NP_GetEntryPointsDelegate? NP_GetEntryPoints;
        private static NPAPIProcs.NP_InitializeDelegate? NP_Initialize;
        public static NPAPIProcs.NP_ShutdownDelegate? NP_Shutdown;

        public static IntPtr nppUnmanagedPtr = IntPtr.Zero;
        public static IntPtr mimePtr = IntPtr.Zero;
        public static IntPtr s_savedDataPtr = IntPtr.Zero;
        public static IntPtr s_savedDataPtrPtr = IntPtr.Zero;
        public const ushort NP_VERSION_MAJOR = 27;
        public const ushort NP_VERSION_MINOR = 0;
        public const ushort NP_VERSION = (NP_VERSION_MAJOR << 8) | NP_VERSION_MINOR;
        public static NPNetscapeFuncs NetscapeFuncs;
        public static NPClass BrowserClass;

        public static IntPtr StringToUtf8(string str)
        {
            str ??= "";
            var bytes = System.Text.Encoding.UTF8.GetBytes(str + "\0");
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }

        public static void StartPlugin(IntPtr hwnd)
        {
            Logger.Log($"StartPlugin entered hwnd=0x{hwnd.ToString("x")}");

            s_hwnd = hwnd;
            Network.InitializeWindow(hwnd);
            SetBrowserWindowHandle(hwnd);

            var pNP_GetEntryPoints = GetProcAddress(App.NpUnityDll, "NP_GetEntryPoints");
            var pNP_Initialize = GetProcAddress(App.NpUnityDll, "NP_Initialize");
            var pNP_Shutdown = GetProcAddress(App.NpUnityDll, "NP_Shutdown");
            if (pNP_GetEntryPoints == IntPtr.Zero || pNP_Initialize == IntPtr.Zero || pNP_Shutdown == IntPtr.Zero)
                throw new Exception("Missing NPAPI exports");

            NetscapeFuncs = new NPNetscapeFuncs
            {
                size = (ushort)Marshal.SizeOf<NPNetscapeFuncs>(),
                version = 27
            };
            BrowserClass = new NPClass();
            NPAPIStubs.InitNetscapeFuncs(ref NetscapeFuncs);

            NP_Initialize = Marshal.GetDelegateForFunctionPointer<NP_InitializeDelegate>(pNP_Initialize);
            NP_GetEntryPoints = Marshal.GetDelegateForFunctionPointer<NP_GetEntryPointsDelegate>(pNP_GetEntryPoints);
            NP_Shutdown = Marshal.GetDelegateForFunctionPointer<NP_ShutdownDelegate>(pNP_Shutdown);

            pluginFuncs = new NPPluginFuncs { size = (ushort)Marshal.SizeOf<NPPluginFuncs>(), version = NP_VERSION };

            NP_GetEntryPoints(ref pluginFuncs);
            NP_Initialize(ref NetscapeFuncs);

            string[] argn = {
                "src","width","height","bordercolor","backgroundcolor",
                "disableContextMenu","textcolor","logoimage","progressbarimage","progressframeimage"
            };

            string[] argp = {
                App.Args.MainPathOrAddress ?? "",
                App.Args.WindowWidth.ToString(),
                App.Args.WindowHeight.ToString(),
                "000000","000000","true","ccffff",
                App.NormalizePathOrUrl("assets/img/unity-dexlabs.png", true),
                App.NormalizePathOrUrl("assets/img/unity-loadingbar.png", true),
                App.NormalizePathOrUrl("assets/img/unity-loadingframe.png", true)
            };

            InitPluginDelegates(pluginFuncs);
            Network.InitNetwork(App.Args.MainPathOrAddress ?? string.Empty);

            mimePtr = StringToUtf8("application/vnd.ffuwp");
            argnPtrs = argn.Select(StringToUtf8).ToArray();
            argpPtrs = argp.Select(StringToUtf8).ToArray();

            argnUnmanaged = Marshal.AllocHGlobal(IntPtr.Size * argnPtrs.Length);
            argpUnmanaged = Marshal.AllocHGlobal(IntPtr.Size * argpPtrs.Length);
            for (var i = 0; i < argnPtrs.Length; i++) Marshal.WriteIntPtr(argnUnmanaged, i * IntPtr.Size, argnPtrs[i]);
            Marshal.WriteIntPtr(argnUnmanaged, argnPtrs.Length * IntPtr.Size, IntPtr.Zero);
            for (var i = 0; i < argpPtrs.Length; i++) Marshal.WriteIntPtr(argpUnmanaged, i * IntPtr.Size, argpPtrs[i]);
            Marshal.WriteIntPtr(argpUnmanaged, argpPtrs.Length * IntPtr.Size, IntPtr.Zero);

            nppUnmanagedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NPP_t>());
            Marshal.StructureToPtr(new NPP_t(), nppUnmanagedPtr, false);

            var saved = new NPSavedData();
            s_savedDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NPSavedData>());
            var nppStruct = new NPP_t
            {
                pdata = IntPtr.Zero,
                ndata = IntPtr.Zero
            };

            Marshal.StructureToPtr(nppStruct, nppUnmanagedPtr, false);

            var newpPtr = pluginFuncs.newp;
            if (newpPtr == IntPtr.Zero)
                throw new Exception("pluginFuncs.newp is NULL; cannot call NPP_New.");

            var newpUnmanaged = Marshal.GetDelegateForFunctionPointer<NPP_New_Unmanaged_Cdecl>(newpPtr);

            var newpRet = newpUnmanaged(
                "application/vnd.ffuwp",
                nppUnmanagedPtr,
                1,
                (short)argn.Length,
                argnUnmanaged,
                argpUnmanaged,
                s_savedDataPtr); // ✅ FIXED: pass NPSavedData*, not **

            if (newpRet != 0)
                throw new Exception($"NPP_New returned error code: {newpRet}");
            Logger.Log($"NPP_New returned {newpRet}");

            var setwindow = Marshal.GetDelegateForFunctionPointer<NPP_SetWindow_Unmanaged_Cdecl>(pluginFuncs.setwindow);
            var ret = setwindow(nppUnmanagedPtr, ref s_npWindow);
            Logger.Log($"Initial NPP_SetWindow returned {ret}");

            var getvalue = Marshal.GetDelegateForFunctionPointer<NPP_GetValue_Unmanaged_Cdecl>(pluginFuncs.getvalue);
            var scriptableObjectPtr = IntPtr.Zero;
            const int NPPVpluginScriptableNPObject = 15;
            getvalue(nppUnmanagedPtr, NPPVpluginScriptableNPObject, ref scriptableObjectPtr);

            scriptableObject = scriptableObjectPtr;
            NPAPIStubs.InitializeScriptableObject(scriptableObjectPtr);
            NPAPIStubs.WarmUpScriptableObject(scriptableObjectPtr);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Rect { public int left, top, right, bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out Win32Rect lpRect);
    }
}
