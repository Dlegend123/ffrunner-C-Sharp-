using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ffrunner
{
    public static class InMemorySources
    {
        // Pointers to unmanaged memory
        public static IntPtr serverAddress;
        public static IntPtr assetUrl;

        // Initialize memory like ffrunner.c
        public static void Init()
        {
            // Example content, match your actual Unity expectations
            string loginInfo = $"username={App.Args.AuthId}&password={App.Args.TegId}&server={App.Args.ServerAddress}";
            serverAddress = AllocateCString(loginInfo);

            string assetInfo = App.Args.AssetUrl;
            assetUrl = AllocateCString(assetInfo);
        }

        // Allocate unmanaged memory for a null-terminated C string
        private static IntPtr AllocateCString(string str)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(str + "\0"); // null-terminated
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }

        // Cleanup when done
        public static void Free()
        {
            if (serverAddress != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(serverAddress);
                serverAddress = IntPtr.Zero;
            }

            if (assetUrl != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(assetUrl);
                assetUrl = IntPtr.Zero;
            }
        }
        public static string? Get(string url)
        {
            if (url.Equals("loginInfo.php", StringComparison.OrdinalIgnoreCase))
                return PtrToString(serverAddress);
            if (url.Equals("assetInfo.php", StringComparison.OrdinalIgnoreCase))
                return PtrToString(assetUrl);
            return null;
        }

        private static string? PtrToString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            return Marshal.PtrToStringAnsi(ptr);
        }

    }
}