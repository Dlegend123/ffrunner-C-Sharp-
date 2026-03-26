using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static ffrunner.Structs;

namespace ffrunner
{
    public partial class MainWindow : Window
    {
        private HwndSource? _hwndSource;

        private const int WM_CLOSE = 0x0010;
        private const int WM_DESTROY = 0x0002;
        private const int WM_MOVE = 0x0003;
        private const int WM_SIZE = 0x0005;
        private const int SIZE_MINIMIZED = 1;

        public MainWindow(Arguments args)
        {
            Logger.Log(
                $"MainWindow ctor entered width={args.WindowWidth}, height={args.WindowHeight}, fullscreen={args.Fullscreen}");
            
            InitializeComponent();
            this.Width = args.WindowWidth;
            this.Height = args.WindowHeight;

            if (args.Fullscreen)
            {
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                Topmost = true;
                WindowState = WindowState.Maximized; // WPF fullscreen
            }

            SourceInitialized += OnSourceInitialized; // Hook HWND ready

            Logger.Log(
                $"MainWindow ctor completed ActualWidth={Width}, ActualHeight={Height}, WindowState={WindowState}");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                switch (msg)
                {
                    case WM_CLOSE:
                    case WM_DESTROY:
                        NotifyWindowClosed();
                        break;

                    case WM_SIZE:
                        RedrawPlugin(hwnd);
                        break;

                    case WM_MOVE:
                        Logger.Verbose($"WndProc: window moved (msg=0x{msg:x})");
                        NotifyWindowChanged(hwnd);
                        break;
                }

                // --- Handle custom network message safely ---
                if (msg == Network.SIoMsg && lParam != IntPtr.Zero)
                {
                    GCHandle handle;
                    try
                    {
                        handle = GCHandle.FromIntPtr(lParam);
                    }
                    catch
                    {
                        Logger.Verbose("Invalid GCHandle in WndProc, skipping.");
                        handled = true;
                        return IntPtr.Zero;
                    }

                    if (handle.Target is Network.Request req)
                    {
                        Network.HandleIoProgress(req);

                        // --- Safely free GCHandle only once ---
                        if (req.Completed && req.Handle?.IsAllocated == true)
                        {
                            try
                            {
                                req.Handle.Value.Free();
                                Logger.Verbose($"WndProc freed GCHandle for requestId={req.Id}");
                            }
                            catch
                            {
                                Logger.Verbose($"WndProc: GCHandle already freed for requestId={req.Id}");
                            }
                            finally
                            {
                                req.Handle = null;
                            }
                        }
                        else
                        {
                            req.ReadyEvent.Set();
                        }
                    }

                    handled = true;
                    return IntPtr.Zero;
                }

            }
            catch (Exception ex)
            {
                Logger.Log($"WndProc exception: {ex}");
            }

            return IntPtr.Zero;
        }

        // Call this when the window needs to repaint
        public static void RedrawPlugin(IntPtr hwnd)
        {
            if (PluginBootstrap.pluginFuncs.setwindow == IntPtr.Zero)
                return;

            Logger.Verbose($"RedrawPlugin called for hwnd=0x{hwnd.ToString("x")}");

            // Ensure window is realized before querying client rect
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                if (GetClientRect(hwnd, out var rect))
                {
                    PluginBootstrap.s_npWindow.x = 0;
                    PluginBootstrap.s_npWindow.y = 0;
                    PluginBootstrap.s_npWindow.width = (uint)Math.Max(0, rect.right - rect.left);
                    PluginBootstrap.s_npWindow.height = (uint)Math.Max(0, rect.bottom - rect.top);
                    PluginBootstrap.s_npWindow.clipRect.top = 0;
                    PluginBootstrap.s_npWindow.clipRect.left = 0;
                    PluginBootstrap.s_npWindow.clipRect.right =
                        (ushort)Math.Min(ushort.MaxValue, rect.right - rect.left);
                    PluginBootstrap.s_npWindow.clipRect.bottom =
                        (ushort)Math.Min(ushort.MaxValue, rect.bottom - rect.top);

                    var setwindow = Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_SetWindow_Unmanaged_Cdecl>(
                        PluginBootstrap.pluginFuncs.setwindow
                    );
                    var ret = setwindow(PluginBootstrap.nppUnmanagedPtr, ref PluginBootstrap.s_npWindow);
                    Logger.Verbose($"RedrawPlugin: NPP_SetWindow returned {ret}");
                }
            }
        }

        public static void NotifyWindowChanged(IntPtr hwnd, bool minimized = false)
        {
            Logger.Verbose($"NotifyWindowChanged entered hwnd=0x{hwnd.ToString("x")}, minimized={minimized}");
            lock (PluginBootstrap.s_windowChangeLock)
            {
                if (PluginBootstrap.pluginFuncs.setwindow == IntPtr.Zero ||
                    PluginBootstrap.nppUnmanagedPtr == IntPtr.Zero)
                    return;

                PluginBootstrap.s_pendingWindowHwnd = hwnd != IntPtr.Zero ? hwnd : PluginBootstrap.s_hwnd;
                PluginBootstrap.s_pendingWindowMinimized = minimized;

                if (PluginBootstrap.s_windowChangePending)
                    return;

                PluginBootstrap.s_windowChangePending = true;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.BeginInvoke((Action)FlushPendingWindowChange,
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            else
            {
                FlushPendingWindowChange();
            }
        }

        private static void FlushPendingWindowChange()
        {
            try
            {
                while (true)
                {
                    IntPtr hwnd;
                    bool minimized;

                    lock (PluginBootstrap.s_windowChangeLock)
                    {
                        hwnd = PluginBootstrap.s_pendingWindowHwnd;
                        minimized = PluginBootstrap.s_pendingWindowMinimized;
                        PluginBootstrap.s_windowChangePending = false;
                    }

                    if (PluginBootstrap.pluginFuncs.setwindow == IntPtr.Zero ||
                        PluginBootstrap.nppUnmanagedPtr == IntPtr.Zero)
                        return;

                    PluginBootstrap.s_hwnd = hwnd != IntPtr.Zero ? hwnd : PluginBootstrap.s_hwnd;

                    var nextWindow = PluginBootstrap.s_npWindow;
                    nextWindow.window = PluginBootstrap.s_hwnd;
                    nextWindow.type = (uint)Structs.NPWindowType.Window;
                    nextWindow.clipRect.top = 0;
                    nextWindow.clipRect.left = 0;

                    if (minimized)
                    {
                        nextWindow.clipRect.right = 0;
                        nextWindow.clipRect.bottom = 0;
                    }
                    else if (PluginBootstrap.s_hwnd != IntPtr.Zero &&
                             GetClientRect(PluginBootstrap.s_hwnd, out var rect))
                    {
                        nextWindow.x = (uint)rect.left;
                        nextWindow.y = (uint)rect.top;
                        nextWindow.width = (uint)Math.Max(0, rect.right - rect.left);
                        nextWindow.height = (uint)Math.Max(0, rect.bottom - rect.top);
                        nextWindow.clipRect.right = (ushort)Math.Min(ushort.MaxValue, nextWindow.width);
                        nextWindow.clipRect.bottom = (ushort)Math.Min(ushort.MaxValue, nextWindow.height);
                    }

                    if (NPWindowEquals(in nextWindow, in PluginBootstrap.s_npWindow))
                    {
                        lock (PluginBootstrap.s_windowChangeLock)
                        {
                            if (!PluginBootstrap.s_windowChangePending)
                                return;
                        }

                        continue;
                    }

                    PluginBootstrap.s_npWindow = nextWindow;

                    var setwindow =
                        Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_SetWindow_Unmanaged_Cdecl>(PluginBootstrap
                            .pluginFuncs.setwindow);
                    var ret = setwindow(PluginBootstrap.nppUnmanagedPtr, ref PluginBootstrap.s_npWindow);
                    Logger.Log($"NotifyWindowChanged: NPP_SetWindow returned {ret}");

                    lock (PluginBootstrap.s_windowChangeLock)
                    {
                        if (!PluginBootstrap.s_windowChangePending)
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
               // Logger.Log($"NotifyWindowChanged threw: {ex}");
            }
        }

        public static void UpdateNPWindowFromWpfWindow()
        {
            if (PluginBootstrap.pluginFuncs.setwindow == IntPtr.Zero || PluginBootstrap.nppUnmanagedPtr == IntPtr.Zero)
                return;

            var widthPx = App.Args.WindowWidth > 0 ? App.Args.WindowWidth : 1280;
            var heightPx = App.Args.WindowHeight > 0 ? App.Args.WindowHeight : 720;

            // --- Setup NPWindow safely ---
            PluginBootstrap.s_npWindow = new Structs.NPWindow
            {
                window = new WindowInteropHelper(App.mainWindow).Handle,
                x = 0,
                y = 0,
                width = (uint)widthPx,
                height = (uint)heightPx,
                type = (uint)Structs.NPWindowType.Window,
                clipRect = new Structs.NPRect
                {
                    top = 0,
                    left = 0,
                    right = (ushort)Math.Min(ushort.MaxValue, widthPx),
                    bottom = (ushort)Math.Min(ushort.MaxValue, heightPx)
                }
            };

            // --- Call NPP_SetWindow safely ---
            try
            {
                var setwindow =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_SetWindow_Unmanaged_Cdecl>(PluginBootstrap
                        .pluginFuncs.setwindow);
                var ret = setwindow(PluginBootstrap.nppUnmanagedPtr, ref PluginBootstrap.s_npWindow);
                Logger.Log($"Set NPWindow: {widthPx}x{heightPx}, NPP_SetWindow returned {ret}");
                App.mainWindow.UpdateLayout();
            }
            catch (Exception ex)
            {
               // Logger.Log($"Error in NPP_SetWindow: {ex}");
            }
        }

        private static bool NPWindowEquals(in Structs.NPWindow left, in Structs.NPWindow right)
        {
            return left.window == right.window
                   && left.x == right.x
                   && left.y == right.y
                   && left.width == right.width
                   && left.height == right.height
                   && left.type == right.type
                   && left.clipRect.top == right.clipRect.top
                   && left.clipRect.left == right.clipRect.left
                   && left.clipRect.right == right.clipRect.right
                   && left.clipRect.bottom == right.clipRect.bottom;
        }

        public static void NotifyWindowClosed()
        {
            Logger.Log("NotifyWindowClosed entered");
            try
            {
                if (PluginBootstrap.pluginFuncs.setwindow == IntPtr.Zero ||
                    PluginBootstrap.nppUnmanagedPtr == IntPtr.Zero)
                    return;

                var setwindow =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_SetWindow_Unmanaged_Cdecl_Ptr>(PluginBootstrap
                        .pluginFuncs.setwindow);
                var ret = setwindow(PluginBootstrap.nppUnmanagedPtr, IntPtr.Zero);
                Logger.Log($"NotifyWindowClosed: NPP_SetWindow(NULL) returned {ret}");
            }
            catch (Exception ex)
            {
               // Logger.Log($"NotifyWindowClosed threw: {ex}");
            }
        }

        public static void ShutdownPlugin()
        {
            Logger.Log("ShutdownPlugin entered");
            try
            {
                Network.Shutdown();

                // Destroy plugin instance first
                if (NPAPIStubs.Plugin_Destroy != null && PluginBootstrap.nppUnmanagedPtr != IntPtr.Zero)
                {
                    if (PluginBootstrap.s_savedDataPtrPtr == IntPtr.Zero)
                    {
                        PluginBootstrap.s_savedDataPtrPtr = Marshal.AllocHGlobal(IntPtr.Size);
                        Marshal.WriteIntPtr(PluginBootstrap.s_savedDataPtrPtr, PluginBootstrap.s_savedDataPtr);
                    }

                    var destroyRet = NPAPIStubs.Plugin_Destroy(PluginBootstrap.nppUnmanagedPtr,
                        PluginBootstrap.s_savedDataPtrPtr);
                    Logger.Log($"NPP_Destroy returned {destroyRet}");
                }

                // --- CALL NP_Shutdown AFTER DESTROY ---
                if (PluginBootstrap.NP_Shutdown != null)
                {
                    PluginBootstrap.NP_Shutdown();
                    Logger.Log("NP_Shutdown called successfully");
                }
            }
            catch (Exception ex)
            {
               // Logger.Log($"ShutdownPlugin error: {ex}");
            }

            if (PluginBootstrap.s_savedDataPtrPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(PluginBootstrap.s_savedDataPtrPtr);
                PluginBootstrap.s_savedDataPtrPtr = IntPtr.Zero;
            }

            if (PluginBootstrap.s_savedDataPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(PluginBootstrap.s_savedDataPtr);
                PluginBootstrap.s_savedDataPtr = IntPtr.Zero;
            }

            FreeInitBuffers();

            // Free NPAPI stub allocations
            NPAPIStubs.Cleanup();
            Logger.Log("ShutdownPlugin completed");
        }
        // Free buffers previously kept for plugin lifetime
        public static void FreeInitBuffers()
        {
            Logger.Log("FreeInitBuffers entered");

            foreach (var p in PluginBootstrap.argnPtrs)
                if (p != IntPtr.Zero)
                    Marshal.FreeHGlobal(p);
            PluginBootstrap.argnPtrs = Array.Empty<IntPtr>();

            foreach (var p in PluginBootstrap.argpPtrs)
                if (p != IntPtr.Zero)
                    Marshal.FreeHGlobal(p);
            PluginBootstrap.argpPtrs = Array.Empty<IntPtr>();

            if (PluginBootstrap.argnUnmanaged != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(PluginBootstrap.argnUnmanaged);
                PluginBootstrap.argnUnmanaged = IntPtr.Zero;
            }

            if (PluginBootstrap.argpUnmanaged != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(PluginBootstrap.argpUnmanaged);
                PluginBootstrap.argpUnmanaged = IntPtr.Zero;
            }

            if (PluginBootstrap.nppUnmanagedPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(PluginBootstrap.nppUnmanagedPtr);
                PluginBootstrap.nppUnmanagedPtr = IntPtr.Zero;
            }

            if (PluginBootstrap.mimePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(PluginBootstrap.mimePtr);
                PluginBootstrap.mimePtr = IntPtr.Zero;
            }

            Logger.Log("FreeInitBuffers completed");
        }
        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            Logger.Log("OnSourceInitialized entered");

            var hwnd = new WindowInteropHelper(this).Handle;
            Logger.Log($"HWND acquired early: 0x{hwnd.ToString("x")}");

            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            // ✅ Ensure the WPF window is fully realized before calling GetClientRect
            this.Dispatcher.Invoke(() =>
            {
                this.UpdateLayout();     // Forces layout pass
                this.Activate();         // Brings window to foreground
            });

            try
            {
                PluginBootstrap.StartPlugin(hwnd); // Now safe to call GetClientRect inside StartPlugin
                UpdateNPWindowFromWpfWindow();
            }
            catch (Exception ex)
            {
                Logger.Log($"StartPlugin failed: {ex}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Logger.Log("MainWindow.OnClosed entered");
            _hwndSource?.RemoveHook(WndProc);

            try
            {
                ShutdownPlugin();
            }
            catch (Exception ex)
            {
              //  Logger.Log($"ShutdownPlugin failed: {ex}");
            }

            base.OnClosed(e);
            Application.Current.Shutdown();
        }

        // Replace these:
        // public enum NPWindowType { Window = 0, Drawable = 1 }
        // public struct RECT { public int left, top, right, bottom; }
        // with this Win32-only rect (for GetClientRect):
        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Rect
        {
            public int left, top, right, bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out Win32Rect lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
    }
}