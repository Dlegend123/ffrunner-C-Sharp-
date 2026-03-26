using System;
using System.Runtime.InteropServices;
using static ffrunner.Structs;

namespace ffrunner
{
    // NPAPI delegates and signatures (match npfunctions.h)
    public static class NPAPIProcs
    {
        // ---------------------------
        // Plugin-provided NP_* (Cdecl)
        // ---------------------------
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NP_GetEntryPointsDelegate(ref NPPluginFuncs pluginFuncs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NP_InitializeDelegate(ref NPNetscapeFuncs bFuncs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NP_ShutdownDelegate();

        // ---------------------------
        // Plugin NPP_* (Cdecl)
        // ---------------------------
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPP_New_Unmanaged_Cdecl(
            IntPtr pluginType,      // const char* MIME type → IntPtr for safety
            IntPtr instance,        // NPP_t*
            ushort mode,            // uint16_t
            short argc,             // int16_t
            IntPtr argn,            // char*[]
            IntPtr argv,            // char*[]
            IntPtr saved            // NPSavedData**
        );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPP_Destroy_Unmanaged_Cdecl(IntPtr instance, IntPtr savedPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPP_SetWindow_Unmanaged_Cdecl(IntPtr instance, ref NPWindow window);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPP_SetWindow_Unmanaged_Cdecl_Ptr(IntPtr instance, IntPtr windowPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPP_NewStream_Unmanaged_Cdecl(IntPtr instance, IntPtr type, IntPtr stream, int seekable, out ushort stype);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPP_DestroyStream_Unmanaged_Cdecl(IntPtr instance, IntPtr streamPtr, short reason);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int NPP_WriteReady_Unmanaged_Cdecl(IntPtr instance, IntPtr streamPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int NPP_Write_Unmanaged_Cdecl(IntPtr instance, IntPtr streamPtr, int offset, int len, IntPtr buffer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void NPP_StreamAsFile_Unmanaged_Cdecl(IntPtr instance, IntPtr streamPtr, IntPtr fileNamePtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void NPP_Print_Unmanaged_Cdecl(IntPtr instance, IntPtr platformPrintPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPP_HandleEvent_Unmanaged_Cdecl(IntPtr instance, IntPtr eventPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPP_GetValue_Unmanaged_Cdecl(IntPtr instance, int variable, ref IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void NPP_URLNotifyDelegate(IntPtr instance,
            [MarshalAs(UnmanagedType.LPStr)] string url,
            short reason,
            IntPtr notifyData);

        // ---------------------------
        // Browser-provided NPN_* (Cdecl)
        // ---------------------------
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public delegate string NPN_UserAgentDelegate(IntPtr instance);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPN_GetURLDelegate(IntPtr instance, IntPtr urlPtr, IntPtr windowPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPN_GetURLNotifyDelegate(IntPtr instance, IntPtr urlPtr, IntPtr windowPtr, IntPtr notifyData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPN_PostURLDelegate(IntPtr instance, IntPtr urlPtr, IntPtr windowPtr, uint len, IntPtr buf, [MarshalAs(UnmanagedType.I1)] bool file);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPN_PostURLNotifyDelegate(IntPtr instance, IntPtr urlPtr, IntPtr windowPtr, uint len, IntPtr buf, [MarshalAs(UnmanagedType.I1)] bool file, IntPtr notifyData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr NPN_GetStringIdentifierDelegate(IntPtr namePtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void NPN_GetStringIdentifiersDelegate(IntPtr namesPtr, int nameCount, IntPtr identifiersPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr NPN_CreateObjectDelegate(IntPtr nppPtr, IntPtr npClassPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr NPN_RetainObjectDelegate(IntPtr objPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void NPN_ReleaseObjectDelegate(IntPtr objPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NPN_InvokeDelegate(IntPtr npp, IntPtr obj, IntPtr methodName, IntPtr argsPtr, uint argCount, IntPtr resultPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NPN_EvaluateDelegate(IntPtr npp, IntPtr obj, IntPtr scriptPtr, IntPtr resultPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NPN_GetPropertyDelegate(IntPtr npp, IntPtr obj, IntPtr propertyName, IntPtr resultPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void NPN_ReleaseVariantValueProcPtr(IntPtr variantPtr);

        // ---------------------------
        // NPClass callbacks (Cdecl)
        // ---------------------------
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr NP_AllocateDelegate(IntPtr instancePtr, IntPtr aClassPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void NP_DeallocateDelegate(IntPtr npobjPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void NP_InvalidateDelegate(IntPtr npobjPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NP_HasMethodDelegate(IntPtr npobjPtr, IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NP_HasPropertyDelegate(IntPtr npobjPtr, IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NP_InvokeDelegate(IntPtr npobj, IntPtr name, IntPtr argsPtr, uint argCount, IntPtr resultPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NP_InvokeDefaultDelegate(IntPtr npobj, IntPtr argsPtr, uint argCount, IntPtr resultPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NP_GetPropertyDelegate(IntPtr npobj, IntPtr name, IntPtr resultPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NP_SetPropertyDelegate(IntPtr npobj, IntPtr name, IntPtr resultPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NP_RemovePropertyDelegate(IntPtr npobj, IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NP_EnumerateDelegate(IntPtr npobj, ref IntPtr identifiersPtr, ref uint count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool NP_ConstructDelegate(IntPtr npobj, IntPtr argsPtr, uint argCount, IntPtr resultPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate short NPN_GetValueDelegate(IntPtr instance, int variable, IntPtr valuePtr);
    }
}