using System.Runtime.InteropServices;

namespace ffrunner
{
    public class Structs
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct NPObject
        {
            public IntPtr _class;
            public uint referenceCount;
        }

        public enum NPError : short
        {
            NPERR_NO_ERROR = 0,
            NPERR_GENERIC_ERROR = 1,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NPPluginFuncs
        {
            public ushort size;
            public ushort version;

            public IntPtr newp;              // NPP_NewProcPtr
            public IntPtr destroy;           // NPP_DestroyProcPtr
            public IntPtr setwindow;         // NPP_SetWindowProcPtr
            public IntPtr newstream;         // NPP_NewStreamProcPtr
            public IntPtr destroystream;     // NPP_DestroyStreamProcPtr
            public IntPtr asfile;            // NPP_StreamAsFileProcPtr
            public IntPtr writeready;        // NPP_WriteReadyProcPtr
            public IntPtr write;             // NPP_WriteProcPtr
            public IntPtr print;             // NPP_PrintProcPtr
            public IntPtr eventProc;         // NPP_HandleEventProcPtr
            public IntPtr urlnotify;         // NPP_URLNotifyProcPtr
            public IntPtr javaClass;         // void*
            public IntPtr getvalue;          // NPP_GetValueProcPtr
            public IntPtr setvalue;          // NPP_SetValueProcPtr
            public IntPtr gotfocus;          // NPP_GotFocusPtr
            public IntPtr lostfocus;         // NPP_LostFocusPtr
            public IntPtr urlredirectnotify; // NPP_URLRedirectNotifyPtr
            public IntPtr clearsitedata;     // NPP_ClearSiteDataPtr
            public IntPtr getsiteswith;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NPClass
        {
            public uint structVersion;

            public IntPtr allocate;       // NPAllocateFunctionPtr
            public IntPtr deallocate;     // NPDeallocateFunctionPtr
            public IntPtr invalidate;     // NPInvalidateFunctionPtr
            public IntPtr hasMethod;      // NPHasMethodFunctionPtr
            public IntPtr invoke;         // NPInvokeFunctionPtr
            public IntPtr invokeDefault;  // NPInvokeDefaultFunctionPtr
            public IntPtr hasProperty;    // NPHasPropertyFunctionPtr
            public IntPtr getProperty;    // NPGetPropertyFunctionPtr
            public IntPtr setProperty;    // NPSetPropertyFunctionPtr
            public IntPtr removeProperty; // NPRemovePropertyFunctionPtr
            public IntPtr enumerate;      // NPEnumerationFunctionPtr
            public IntPtr construct;      // NPConstructFunctionPtr
        }


        [StructLayout(LayoutKind.Sequential)]
        public struct NPNetscapeFuncs
        {
            public ushort size;
            public ushort version;

            public IntPtr geturl;
            public IntPtr posturl;
            public IntPtr requestread;
            public IntPtr newstream;
            public IntPtr write;
            public IntPtr destroystream;
            public IntPtr status;
            public IntPtr uagent;
            public IntPtr memalloc;
            public IntPtr memfree;
            public IntPtr memflush;
            public IntPtr reloadplugins;
            public IntPtr getJavaEnv;
            public IntPtr getJavaPeer;
            public IntPtr geturlnotify;
            public IntPtr posturlnotify;
            public IntPtr getvalue;
            public IntPtr setvalue;
            public IntPtr invalidaterect;
            public IntPtr invalidateregion;
            public IntPtr forceredraw;
            public IntPtr getstringidentifier;
            public IntPtr getstringidentifiers;
            public IntPtr getintidentifier;
            public IntPtr identifierisstring;
            public IntPtr utf8fromidentifier;
            public IntPtr intfromidentifier;
            public IntPtr createobject;
            public IntPtr retainobject;
            public IntPtr releaseobject;
            public IntPtr invoke;
            public IntPtr invokeDefault;
            public IntPtr evaluate;
            public IntPtr getproperty;
            public IntPtr setproperty;
            public IntPtr removeproperty;
            public IntPtr hasproperty;
            public IntPtr hasmethod;
            public IntPtr releasevariantvalue;
            public IntPtr setexception;
            public IntPtr pushpopupsenabledstate;
            public IntPtr poppopupsenabledstate;
            public IntPtr enumerate;
            public IntPtr pluginthreadasynccall;
            public IntPtr construct;
            public IntPtr getvalueforurl;
            public IntPtr setvalueforurl;
            public IntPtr getauthenticationinfo;
            public IntPtr scheduletimer;
            public IntPtr unscheduletimer;
            public IntPtr popupcontextmenu;
            public IntPtr convertpoint;
            public IntPtr handleevent;
            public IntPtr unfocusinstance;
            public IntPtr urlredirectresponse;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NPP_t
        {
            public IntPtr pdata;
            public IntPtr ndata;
        }

        // Matches npapi.h (uint16 top/left/bottom/right)
        [StructLayout(LayoutKind.Sequential)]
        public struct NPRect
        {
            public ushort top;
            public ushort left;
            public ushort bottom;
            public ushort right;
        }

        // Matches npapi.h values (Window=1, Drawable=2)
        public enum NPWindowType
        {
            Window = 1,
            Drawable = 2,
        }
        
        // Matches npapi.h layout: clipRect then type (type is LAST)
        [StructLayout(LayoutKind.Sequential)]
        public struct NPWindow
        {
            public IntPtr window;
            public int x;
            public int y;
            public uint width;
            public uint height;
            public NPRect clipRect;
            public NPWindowType type;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct NPVariant
        {
            [FieldOffset(0)] public NPVariantType type;
            [FieldOffset(8)] public NPVariantValue value;

            [StructLayout(LayoutKind.Explicit)]
            public struct NPVariantValue
            {
                [FieldOffset(0)] public bool boolValue;
                [FieldOffset(0)] public int intValue;
                [FieldOffset(0)] public double doubleValue;
                [FieldOffset(0)] public NPString stringValue;
                [FieldOffset(0)] public IntPtr objectValue; // NPObject*
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NPSavedData
        {
            public int len;
            public IntPtr buf;
        }

        public enum NPVariantType
        {
            Void = 0,
            Null = 1,
            Bool = 2,
            Int32 = 3,
            Double = 4,
            String = 5,
            Object = 6,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NPString
        {
            public IntPtr UTF8Characters;
            public uint UTF8Length;
        }
    }
}