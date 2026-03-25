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

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct NPPluginFuncs
        {
            public ushort size;      // must match sizeof(NPPluginFuncs)
            public ushort version;   // NPAPI version

            public IntPtr newp;
            public IntPtr destroy;
            public IntPtr setwindow;
            public IntPtr newstream;
            public IntPtr destroystream;
            public IntPtr streamasfile;
            public IntPtr writeready;
            public IntPtr write;
            public IntPtr eventproc;
            public IntPtr urlnotify;
            public IntPtr getvalue;
            public IntPtr setvalue;
        }


        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct NPClass
        {
            public uint structVersion;
            public IntPtr allocate;
            public IntPtr deallocate;
            public IntPtr invalidate;
            public IntPtr hasMethod;
            public IntPtr invoke;
            public IntPtr invokeDefault;
            public IntPtr hasProperty;
            public IntPtr getProperty;
            public IntPtr setProperty;
            public IntPtr removeProperty;
            public IntPtr enumerate;
            public IntPtr construct;
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