using System;
using System.Collections.Generic;
using System;
using System.Runtime.InteropServices;

namespace ffrunner
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct LoginInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Username;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Token;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct AssetInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AssetUrl;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string MainPathOrAddress;
    }

    public static class PluginMemory
    {
        public static IntPtr MemoryBlock = IntPtr.Zero;
        public static int Size => Marshal.SizeOf<AssetInfo>() + Marshal.SizeOf<LoginInfo>();

        public static void InitMemory(string assetUrl, string mainPath, string username, string token)
        {
            // Free old memory if it exists
            if (MemoryBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(MemoryBlock);
                MemoryBlock = IntPtr.Zero;
            }

            MemoryBlock = Marshal.AllocHGlobal(Size);

            // Fill AssetInfo
            AssetInfo assetInfo = new AssetInfo
            {
                AssetUrl = assetUrl ?? string.Empty,
                MainPathOrAddress = mainPath ?? string.Empty
            };

            Marshal.StructureToPtr(assetInfo, MemoryBlock, false);

            // Fill LoginInfo immediately after AssetInfo
            LoginInfo loginInfo = new LoginInfo
            {
                Username = username ?? string.Empty,
                Token = token ?? string.Empty
            };

            IntPtr loginPtr = IntPtr.Add(MemoryBlock, Marshal.SizeOf<AssetInfo>());
            Marshal.StructureToPtr(loginInfo, loginPtr, false);

            Logger.Log($"PluginMemory initialized at {MemoryBlock} (AssetInfo + LoginInfo)");
        }

        public static void FreeMemory()
        {
            if (MemoryBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(MemoryBlock);
                MemoryBlock = IntPtr.Zero;
                Logger.Log("PluginMemory freed");
            }
        }

        public static AssetInfo GetAssetInfo()
        {
            return Marshal.PtrToStructure<AssetInfo>(MemoryBlock);
        }

        public static LoginInfo GetLoginInfo()
        {
            IntPtr loginPtr = IntPtr.Add(MemoryBlock, Marshal.SizeOf<AssetInfo>());
            return Marshal.PtrToStructure<LoginInfo>(loginPtr);
        }
    }
}