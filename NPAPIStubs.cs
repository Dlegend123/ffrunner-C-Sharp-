using System.Diagnostics;
using System.Formats.Asn1;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using static ffrunner.NPAPIProcs;
using static ffrunner.Structs;
using NPClass = ffrunner.Structs.NPClass;
using NPObject = ffrunner.Structs.NPObject;

namespace ffrunner
{
    public static class NPAPIStubs
    {
        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        // Keep delegates alive to prevent GC collection for plugin lifetime
        public static readonly List<Delegate> pinnedDelegates = new();


        // Exposed plugin delegate instances (marshaled from plugin function pointers)
        public static NPAPIProcs.NPP_New_Unmanaged_Cdecl_ShortArg? Plugin_New;
        public static NPAPIProcs.NPP_Destroy_Unmanaged_Cdecl? Plugin_Destroy;
        public static NPAPIProcs.NPP_SetWindow_Unmanaged_Cdecl? Plugin_SetWindow;
        public static NPAPIProcs.NPP_GetValue_Unmanaged_Cdecl? Plugin_GetValue;
        public static NPAPIProcs.NPP_URLNotifyDelegate? Plugin_UrlNotify;
        public static NPAPIProcs.NPP_URLNotifyDelegate_Ptr? Plugin_UrlNotifyPtr;

        // Streaming plugin functions
        public static NPAPIProcs.NPP_NewStream_Unmanaged_Cdecl? Plugin_NewStream;
        public static NPAPIProcs.NPP_DestroyStream_Unmanaged_Cdecl? Plugin_DestroyStream;
        public static NPAPIProcs.NPP_WriteReady_Unmanaged_Cdecl? Plugin_WriteReady;
        public static NPAPIProcs.NPP_Write_Unmanaged_Cdecl? Plugin_Write;
        public static NPAPIProcs.NPP_StreamAsFile_Unmanaged_Cdecl? Plugin_StreamAsFile;
        public static NPAPIProcs.NPP_Print_Unmanaged_Cdecl? Plugin_Print;
        public static NPAPIProcs.NPP_HandleEvent_Unmanaged_Cdecl? Plugin_HandleEvent;

        // NPObject / NPClass for browser
        public static NPObject browserObject;

        // Unmanaged pointers used by stubs and returned to plugin (must persist)
        public static IntPtr s_browserClassPtr = IntPtr.Zero;
        public static IntPtr s_browserObjectPtr = IntPtr.Zero;
        private static string s_locationHref = string.Empty;

        // Identifier interning storage
        private static readonly Dictionary<string, IntPtr> s_identifierMap = new(StringComparer.Ordinal);
        private static readonly List<IntPtr> s_allocatedIdentifierPtrs = new();
        private static readonly List<IntPtr> s_allocatedObjectPtrs = new();

        // Track location objects and variant string allocations
        private static readonly List<IntPtr> s_locationObjectPtrs = new();
        private static readonly List<IntPtr> s_allocatedVariantStrings = new();

        // Pinned user agent bytes and delegate
        private static readonly byte[] userAgentBytes = Encoding.ASCII.GetBytes("ffrunner.exe\0");
        private static GCHandle userAgentHandle = GCHandle.Alloc(userAgentBytes, GCHandleType.Pinned);
        private static IntPtr userAgentPtr = IntPtr.Zero;
        private static NPAPIProcs.NPN_UserAgentDelegate? uagentDel;
        private static IntPtr s_browserWindowHandle = IntPtr.Zero;

        // Pin helper
        private static T PinDelegate<T>(T del) where T : Delegate
        {
            pinnedDelegates.Add(del);
            return del;
        }

        private static string DescribeNPString(Structs.NPString value)
        {
            try
            {
                if (value.UTF8Characters == IntPtr.Zero)
                    return "(null)";

                if (!IsReadablePointer(value.UTF8Characters))
                    return $"<unreadable:0x{value.UTF8Characters:x}>";

                int length = checked((int)Math.Min(value.UTF8Length, 512u));
                byte[] bytes = new byte[length];
                if (length > 0)
                    Marshal.Copy(value.UTF8Characters, bytes, 0, length);

                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                return $"<npstring-error:{ex.GetType().Name}>";
            }
        }

        private static string DescribeNPIdentifier(IntPtr identifier)
        {
            if (identifier == IntPtr.Zero)
                return "(null)";

            try
            {
                bool isInt = (identifier & 1) == 1;
                if (isInt)
                    return $"int:{(((nint)identifier) >> 1)}";

                if (!IsReadablePointer(identifier))
                    return $"ptr:0x{identifier:x}";

                string text = ReadAnsiString(identifier, 128);
                return string.IsNullOrEmpty(text) ? $"ptr:0x{identifier.ToString("x")}" : $"str:'{text}'";
            }
            catch (Exception ex)
            {
                return $"<identifier-error:{ex.GetType().Name}>";
            }
        }

        private static string DescribeNPObjectRefCount(IntPtr objPtr)
        {
            if (objPtr == IntPtr.Zero)
                return "(null)";

            try
            {
                if (!IsReadablePointer(objPtr))
                    return $"0x{objPtr.ToString("x")} unreadable";

                var obj = Marshal.PtrToStructure<NPObject>(objPtr);
                return $"0x{objPtr.ToString("x")} class=0x{obj._class.ToString("x")} refCount={obj.referenceCount}";
            }
            catch (Exception ex)
            {
                return $"0x{objPtr.ToString("x")} <obj-error:{ex.GetType().Name}>";
            }
        }

        private static void WriteVoidVariant(IntPtr resultPtr)
        {
            try
            {
                if (resultPtr == IntPtr.Zero)
                    return;
                var result = new NPVariant
                {
                    type = NPVariantType.Void,
                    value = new NPVariant.NPVariantValue { objectValue = IntPtr.Zero }
                };
                Marshal.StructureToPtr(result, resultPtr, true); // true ensures old data is cleaned up
                Logger.Log($"Wrote Void variant to 0x{resultPtr:x}");
            }
            catch (Exception ex)
            {
                Logger.Log($"WriteVoidVariant threw: {ex}");
            }
        }

        private static bool IdentifierEqualsString(IntPtr identifier, string expected)
        {
            if (identifier == IntPtr.Zero)
                return false;

            if ((((nint)identifier) & 1) == 1)
                return false;

            if (!IsReadablePointer(identifier))
                return false;

            string actual = ReadAnsiString(identifier, 128);
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }

        private static bool IsLocationObject(IntPtr objPtr)
        {
            if (objPtr == IntPtr.Zero)
                return false;

            lock (s_locationObjectPtrs)
            {
                return s_locationObjectPtrs.Contains(objPtr);
            }
        }

        private static string DescribeNPVariant(in Structs.NPVariant value)
        {
            try
            {
                return value.type switch
                {
                    NPVariantType.Void => "Void",
                    NPVariantType.Null => "Null",
                    NPVariantType.Bool => $"Bool({value.value.boolValue})",
                    NPVariantType.Int32 => $"Int32({value.value.intValue})",
                    NPVariantType.Double => $"Double({value.value.doubleValue})",
                    NPVariantType.String => $"String('{DescribeNPString(value.value.stringValue)}')",
                    NPVariantType.Object => $"Object(0x{value.value.objectValue:x})",
                    _ => $"Unknown({(uint)value.type}, object=0x{value.value.objectValue:x})"
                };
            }
            catch (Exception ex)
            {
                return $"<npvariant-error:{ex.GetType().Name}>";
            }
        }

        private static string DescribeNPVariantPtr(IntPtr variantPtr)
        {
            if (variantPtr == IntPtr.Zero)
                return "(null)";

            if (!IsReadablePointer(variantPtr))
                return $"<unreadable:0x{variantPtr:x}>";

            try
            {
                var value = Marshal.PtrToStructure<Structs.NPVariant>(variantPtr);
                return DescribeNPVariant(value);
            }
            catch (Exception ex)
            {
                return $"<npvariantptr-error:{ex.GetType().Name}>";
            }
        }

        private static string DescribeNPVariantsBuffer(IntPtr argsPtr, uint argCount)
        {
            if (argsPtr == IntPtr.Zero || argCount == 0)
                return "[]";

            try
            {
                int size = Marshal.SizeOf<Structs.NPVariant>();
                string[] parts = new string[argCount];
                for (int i = 0; i < argCount; i++)
                {
                    IntPtr current = IntPtr.Add(argsPtr, i * size);
                    parts[i] = DescribeNPVariantPtr(current);
                }

                return "[" + string.Join(", ", parts) + "]";
            }
            catch (Exception ex)
            {
                return $"<npvariantbuffer-error:{ex.GetType().Name}>";
            }
        }
        public static short NPN_GetValue(IntPtr instance, int variable, IntPtr retValue)
        {
            Logger.Log($"NPN_GetValue variable={variable}");

            if (retValue == IntPtr.Zero)
                return (short)Structs.NPError.NPERR_GENERIC_ERROR;

            if (s_browserObjectPtr == IntPtr.Zero)
                return (short)Structs.NPError.NPERR_GENERIC_ERROR;

            // Increment refcount
            browserObject.referenceCount++;

            // ✅ Just write the pointer to the NPObject
            Marshal.WriteIntPtr(retValue, s_browserObjectPtr);

            Logger.Log($"NPN_GetValue returning obj=0x{s_browserObjectPtr.ToInt64():x} refCount={browserObject.referenceCount}");
            return (short)Structs.NPError.NPERR_NO_ERROR;
        }
        public static void SetBrowserWindowHandle(IntPtr hwnd)
        {
            Logger.Log($"SetBrowserWindowHandle hwnd=0x{hwnd.ToString("x")}");
            s_browserWindowHandle = hwnd;
        }

        public static short NPP_DestroyStub(IntPtr instance, IntPtr saved)
        {
            Logger.Log("NPP_DestroyStub called");
            return 0; // NPERR_NO_ERROR
        }

        public static IntPtr NPN_CreateObject(IntPtr npp, IntPtr aClass)
        {
            Logger.Log($"NPN_CreateObject called npp=0x{npp:x}, class=0x{aClass:x}");

            if (s_browserObjectPtr != IntPtr.Zero)
            {
                Logger.Log("Returning persistent browser NPObject");
                return s_browserObjectPtr;
            }

            // Fallback: allocate a new NPObject
            var npobj = new NPObject { _class = aClass, referenceCount = 1 };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<NPObject>());
            Marshal.StructureToPtr(npobj, p, false);

            lock (s_allocatedObjectPtrs)
            {
                s_allocatedObjectPtrs.Add(p);
            }

            return p;
        }



        // Fill browser NPClass methods and create unmanaged NPClass/NPObject
        public static void FillBrowserFuncs(ref NPClass clazz)
        {
            clazz.structVersion = 3;
            browserObject = new NPObject
            {
                _class = IntPtr.Zero, // set after class is allocated
                referenceCount = 1,
            };

            Logger.Log("FillBrowserFuncs entered");
            if (s_browserClassPtr != IntPtr.Zero)
                return;

            // Minimal browser NPClass behavior: everything returns false / no-op (matches ffrunner.c)
            var allocateDel = PinDelegate<NPAPIProcs.NP_AllocateDelegate>((IntPtr instancePtr, IntPtr aClassPtr) =>
            {
                Logger.Log($"NP_Allocate instance=0x{instancePtr.ToString("x")}, class=0x{aClassPtr.ToString("x")}");
                var npobj = new NPObject
                {
                    _class = aClassPtr,
                    referenceCount = 1,
                };

                IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<NPObject>());
                Marshal.StructureToPtr(npobj, p, false);

                lock (s_allocatedObjectPtrs)
                {
                    s_allocatedObjectPtrs.Add(p);
                }

                return p;
            });

            var deallocateDel = PinDelegate<NPAPIProcs.NP_DeallocateDelegate>((IntPtr npobjPtr) =>
            {
                try
                {
                    Logger.Log($"NP_Deallocate obj=0x{npobjPtr:x}");
                    if (npobjPtr == IntPtr.Zero)
                        return;

                    if (npobjPtr == s_browserObjectPtr)
                        return;

                    bool owned;
                    lock (s_allocatedObjectPtrs)
                    {
                        owned = s_allocatedObjectPtrs.Remove(npobjPtr);
                    }

                    //if (owned)
                    //    Marshal.FreeHGlobal(npobjPtr);
                }
                catch (Exception ex)
                {
                    Logger.Log($"NP_Deallocate threw: {ex}");
                }
            });

            var invalidateDel = PinDelegate<NPAPIProcs.NP_InvalidateDelegate>((IntPtr npobjPtr) =>
            {
                Logger.Log($"NP_Invalidate obj=0x{npobjPtr.ToString("x")}");
            });

            var hasMethodDel = PinDelegate<NPAPIProcs.NP_HasMethodDelegate>((IntPtr npobjPtr, IntPtr name) =>
            {
                Logger.Log($"NP_HasMethod obj=0x{npobjPtr.ToString("x")}, name=0x{name.ToString("x")}");
                return false;
            });
            var hasPropertyDel = PinDelegate<NPAPIProcs.NP_HasPropertyDelegate>((IntPtr npobjPtr, IntPtr name) =>
            {
                Logger.Log($"NP_HasProperty obj=0x{npobjPtr.ToString("x")}, name=0x{name.ToString("x")}");

                if (npobjPtr == s_browserObjectPtr && IdentifierEqualsString(name, "location"))
                    return true;

                if (IsLocationObject(npobjPtr) && IdentifierEqualsString(name, "href"))
                    return true;

                return false;
            });

            var invokeDel = PinDelegate<NPAPIProcs.NP_InvokeDelegate>(
                (IntPtr npobj, IntPtr name, IntPtr argsPtr, uint argCount, IntPtr resultPtr) =>
                {
                    Logger.Log(
                        $"NP_Invoke obj=0x{npobj.ToString("x")}, name=0x{name.ToString("x")}, argc={argCount}, args={DescribeNPVariantsBuffer(argsPtr, argCount)}, resultPtr=0x{resultPtr:x}, resultBefore={DescribeNPVariantPtr(resultPtr)}");
                    return false;
                });

            var invokeDefaultDel = PinDelegate<NPAPIProcs.NP_InvokeDefaultDelegate>(
                (IntPtr npobj, IntPtr argsPtr, uint argCount, IntPtr resultPtr) =>
                {
                    Logger.Log(
                        $"NP_InvokeDefault obj=0x{npobj.ToString("x")}, argc={argCount}, args={DescribeNPVariantsBuffer(argsPtr, argCount)}, resultPtr=0x{resultPtr:x}, resultBefore={DescribeNPVariantPtr(resultPtr)}");
                    return false;
                });

            var getPropertyDel = PinDelegate<NPAPIProcs.NP_GetPropertyDelegate>(
                (IntPtr npobj, IntPtr name, IntPtr resultPtr) =>
                {
                    Logger.Log(
                        $"NP_GetProperty obj=0x{npobj.ToString("x")}, name=0x{name.ToString("x")}, resultPtr=0x{resultPtr.ToString("x")}, resultBefore={DescribeNPVariantPtr(resultPtr)}");

                    return false;
                });

            var setPropertyDel = PinDelegate<NPAPIProcs.NP_SetPropertyDelegate>(
                (IntPtr npobj, IntPtr name, IntPtr resultPtr) =>
                {
                    Logger.Log(
                        $"NP_SetProperty obj=0x{npobj.ToString("x")}, name=0x{name.ToString("x")}, valuePtr=0x{resultPtr.ToString("x")}, value={DescribeNPVariantPtr(resultPtr)}");

                    return false;
                });
            var removePropertyDel = PinDelegate<NPAPIProcs.NP_RemovePropertyDelegate>((IntPtr npobj, IntPtr name) =>
            {
                Logger.Log($"NP_RemoveProperty obj=0x{npobj.ToString("x")}, name=0x{name.ToString("x")}");
                return false;
            });

            var enumerateDel = PinDelegate<NPAPIProcs.NP_EnumerateDelegate>(
                (IntPtr npobj, ref IntPtr identifiersPtr, ref uint count) =>
                {
                    Logger.Log($"NP_Enumerate obj=0x{npobj.ToString("x")}");
                    identifiersPtr = IntPtr.Zero;
                    count = 0;
                    return false;
                });

            var constructDel = PinDelegate<NPAPIProcs.NP_ConstructDelegate>(
                (IntPtr npobj, IntPtr argsPtr, uint argCount, IntPtr resultPtr) =>
                {
                    Logger.Log(
                        $"NP_Construct obj=0x{npobj:x}, argc={argCount}, args={DescribeNPVariantsBuffer(argsPtr, argCount)}, resultPtr=0x{resultPtr:x}, resultBefore={DescribeNPVariantPtr(resultPtr)}");
                    return false;
                });

            // Populate the managed NPClass structure with function pointers

            clazz.allocate = Marshal.GetFunctionPointerForDelegate(allocateDel);
            clazz.deallocate = Marshal.GetFunctionPointerForDelegate(deallocateDel);
            clazz.invalidate = Marshal.GetFunctionPointerForDelegate(invalidateDel);
            clazz.hasMethod = Marshal.GetFunctionPointerForDelegate(hasMethodDel);
            clazz.invoke = Marshal.GetFunctionPointerForDelegate(invokeDel);
            clazz.invokeDefault = Marshal.GetFunctionPointerForDelegate(invokeDefaultDel);
            clazz.hasProperty = Marshal.GetFunctionPointerForDelegate(hasPropertyDel);
            clazz.getProperty = Marshal.GetFunctionPointerForDelegate(getPropertyDel);
            clazz.setProperty = Marshal.GetFunctionPointerForDelegate(setPropertyDel);
            clazz.removeProperty = Marshal.GetFunctionPointerForDelegate(removePropertyDel);
            clazz.enumerate = Marshal.GetFunctionPointerForDelegate(enumerateDel);
            clazz.construct = Marshal.GetFunctionPointerForDelegate(constructDel);

            // Allocate native NPClass
            s_browserClassPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NPClass>());
            Marshal.StructureToPtr(clazz, s_browserClassPtr, false);

            // ✅ Allocate and publish a persistent browser NPObject
            browserObject = new NPObject
            {
                _class = s_browserClassPtr, // assign NPClass pointer
                referenceCount = 1,
            };

            s_browserObjectPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NPObject>());
            Marshal.StructureToPtr(browserObject, s_browserObjectPtr, false);
        }

        public static void InitPluginDelegates(NPPluginFuncs funcs)
        {
            Logger.Log("InitPluginDelegates entered");
            if (funcs.newp != IntPtr.Zero)
            {
                Plugin_New =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_New_Unmanaged_Cdecl_ShortArg>(funcs.newp);
                pinnedDelegates.Add(Plugin_New);
            }

            if (funcs.destroy != IntPtr.Zero)
            {
                Plugin_Destroy =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_Destroy_Unmanaged_Cdecl>(funcs.destroy);
                pinnedDelegates.Add(Plugin_Destroy);
            }

            if (funcs.setwindow != IntPtr.Zero)
            {
                Plugin_SetWindow =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_SetWindow_Unmanaged_Cdecl>(funcs.setwindow);
                pinnedDelegates.Add(Plugin_SetWindow);
            }

            if (funcs.getvalue != IntPtr.Zero)
            {
                Plugin_GetValue =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_GetValue_Unmanaged_Cdecl>(funcs.getvalue);
                pinnedDelegates.Add(Plugin_GetValue);
            }

            if (funcs.urlnotify != IntPtr.Zero)
            {
                Plugin_UrlNotify =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_URLNotifyDelegate>(funcs.urlnotify);
                Plugin_UrlNotifyPtr =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_URLNotifyDelegate_Ptr>(funcs.urlnotify);
                pinnedDelegates.Add(Plugin_UrlNotify);
                pinnedDelegates.Add(Plugin_UrlNotifyPtr);
            }

            // Streaming functions
            if (funcs.newstream != IntPtr.Zero)
            {
                Plugin_NewStream =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_NewStream_Unmanaged_Cdecl>(funcs.newstream);
                pinnedDelegates.Add(Plugin_NewStream);
            }

            if (funcs.destroystream != IntPtr.Zero)
            {
                Plugin_DestroyStream =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_DestroyStream_Unmanaged_Cdecl>(
                        funcs.destroystream);
                pinnedDelegates.Add(Plugin_DestroyStream);
            }


            if (funcs.writeready != IntPtr.Zero)
            {
                Plugin_WriteReady =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_WriteReady_Unmanaged_Cdecl>(funcs.writeready);
                pinnedDelegates.Add(Plugin_WriteReady);
            }

            if (funcs.write != IntPtr.Zero)
            {
                Plugin_Write = Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_Write_Unmanaged_Cdecl>(funcs.write);
                pinnedDelegates.Add(Plugin_Write);
            }

            Logger.Log(
                $"InitPluginDelegates completed newp=0x{funcs.newp.ToString("x")}, destroy=0x{funcs.destroy.ToString("x")}, setwindow=0x{funcs.setwindow.ToString("x")}, getvalue=0x{funcs.getvalue.ToString("x")}, urlnotify=0x{funcs.urlnotify.ToString("x")}");
        }

        public static void InitNetscapeFuncs(ref NPNetscapeFuncs funcs)
        {
            Logger.Log($"InitNetscapeFuncs entered size={funcs.size}, version={funcs.version}");

            // Helper to pin and return delegates
            T pin<T>(T d) where T : Delegate
            {
                pinnedDelegates.Add(d);
                return d;
            }

            // NPN_GetURLProc
            var geturlDel = pin<NPAPIProcs.NPN_GetURLDelegate>((IntPtr instance, IntPtr urlPtr, IntPtr windowPtr) =>
            {
                string url = ReadAnsiString(urlPtr);
                string window = ReadAnsiString(windowPtr);
                Logger.Log($"NPN_GetURL url='{url}', window='{window}'");
                Network.RegisterGetRequest(url, false, IntPtr.Zero);
                return 0; // NPERR_NO_ERROR
            });
            funcs.geturl = Marshal.GetFunctionPointerForDelegate(geturlDel);

            // NPN_PostURLProc
            var posturlDel = pin<NPAPIProcs.NPN_PostURLDelegate>(
                (IntPtr instance, IntPtr urlPtr,
                    IntPtr windowPtr, uint len, IntPtr buf,
                    [MarshalAs(UnmanagedType.I1)] bool file) =>
                {
                    Logger.Log(
                        $"NPN_PostURL called urlPtr=0x{urlPtr.ToString("x")}, windowPtr=0x{windowPtr.ToString("x")}, len={len}, buf=0x{buf.ToString("x")}, file={file}");
                    string url = ReadAnsiString(urlPtr);
                    string window = ReadAnsiString(windowPtr);
                    Logger.Log(
                        $"NPN_PostURL url='{url}', window='{window}', len={len}, file={file}, buf=0x{buf.ToString("x")}");

                    byte[] data = Array.Empty<byte>();
                    uint safeLen = Math.Min(len, 0x1000u);
                    if (safeLen > 0 && buf != IntPtr.Zero && IsReadablePointer(buf))
                    {
                        int n = checked((int)safeLen);
                        data = new byte[n];
                        Marshal.Copy(buf, data, 0, n);
                    }

                    Network.RegisterPostRequest(url, false, IntPtr.Zero, safeLen, data);
                    return 0; // NPERR_NO_ERROR
                });
            funcs.posturl = Marshal.GetFunctionPointerForDelegate(posturlDel);

            // NPN_UserAgentProc
            userAgentPtr = userAgentHandle.AddrOfPinnedObject();
            uagentDel = PinDelegate<NPAPIProcs.NPN_UserAgentDelegate>((IntPtr instance) =>
            {
                Logger.Log($"NPN_UserAgent called, returning 'ffrunner'");
                return userAgentPtr;
            });

            funcs.uagent = Marshal.GetFunctionPointerForDelegate(uagentDel);
            // Prepare user agent delegate and pointer


            // NPN_GetURLNotifyProc
            var geturlNotifyDel = pin<NPAPIProcs.NPN_GetURLNotifyDelegate>(
                (IntPtr instance, IntPtr urlPtr, IntPtr windowPtr, IntPtr notifyData) =>
                {
                    Logger.Log($"NPN_GetURLNotify called notifyData=0x{notifyData.ToString("x")}");
                    string url = ReadAnsiString(urlPtr);
                    string window = ReadAnsiString(windowPtr);
                    Logger.Log(
                        $"NPN_GetURLNotify url='{url}', window='{window}', notifyData=0x{notifyData.ToString("x")}");
                    Network.RegisterGetRequest(url, true, notifyData);
                    return 0; // NPERR_NO_ERROR
                });
            funcs.geturlnotify = Marshal.GetFunctionPointerForDelegate(geturlNotifyDel);
            var invokeStub = pin<NPAPIProcs.NPN_InvokeDelegate>(
                (IntPtr npp, IntPtr obj, IntPtr methodName, IntPtr argsPtr, uint argCount, IntPtr resultPtr) =>
                {
                    Logger.Log(
                        $"NPN_Invoke obj={DescribeNPObjectRefCount(obj)}, method={DescribeNPIdentifier(methodName)}, argc={argCount}, args={DescribeNPVariantsBuffer(argsPtr, argCount)}, resultPtr=0x{resultPtr.ToString("x")}, resultBefore={DescribeNPVariantPtr(resultPtr)}");
                    WriteVoidVariant(resultPtr);
                    return false;
                });
            funcs.invoke = Marshal.GetFunctionPointerForDelegate(invokeStub);
            // NPN_PostURLNotifyProc
            var posturlNotifyDel = pin<NPAPIProcs.NPN_PostURLNotifyDelegate>(
                (IntPtr instance, IntPtr urlPtr,
                    IntPtr windowPtr, uint len, IntPtr buf,
                    [MarshalAs(UnmanagedType.I1)] bool file, IntPtr notifyData) =>
                {
                    string url = ReadAnsiString(urlPtr);
                    string window = ReadAnsiString(windowPtr);
                    Logger.Log(
                        $"NPN_PostURLNotify url='{url}', window='{window}', len={len}, file={file}, notifyData=0x{notifyData.ToString("x")}, buf=0x{buf.ToString("x")}");

                    byte[] data = Array.Empty<byte>();
                    uint safeLen = Math.Min(len, 0x1000u);
                    if (safeLen > 0 && buf != IntPtr.Zero && IsReadablePointer(buf))
                    {
                        int n = checked((int)safeLen);
                        data = new byte[n];
                        Marshal.Copy(buf, data, 0, n);
                    }

                    Network.RegisterPostRequest(url, true, notifyData, safeLen, data);
                    return 0; // NPERR_NO_ERROR
                });
            funcs.posturlnotify = Marshal.GetFunctionPointerForDelegate(posturlNotifyDel);

            // NPN_ReleaseObjectProc
            var releaseObj = pin<NPAPIProcs.NPN_ReleaseObjectDelegate>((IntPtr objPtr) =>
            {
                try
                {
                    Logger.Log($"NPN_ReleaseObject before obj={DescribeNPObjectRefCount(objPtr)}");
                    if (objPtr == IntPtr.Zero) return;
                    if (!IsReadablePointer(objPtr)) return;

                    var obj = Marshal.PtrToStructure<NPObject>(objPtr);
                    if (obj.referenceCount > 0)
                        obj.referenceCount--;

                    if (obj.referenceCount != 0)
                    {
                        Marshal.StructureToPtr(obj, objPtr, false);
                        return;
                    }

                    // should never ask to deallocate the (statically allocated) browser object
                    if (objPtr == s_browserObjectPtr || IsLocationObject(objPtr))
                    {
                        //obj.referenceCount = 1;
                        Logger.Log($"NPN_ReleaseObject dont free browser object: {objPtr}");
                        Marshal.StructureToPtr(obj, objPtr, false);
                        return;
                    }

                    // if (obj->_class && obj->_class->deallocate) obj->_class->deallocate(obj); else free(obj);
                    IntPtr classPtr = obj._class;
                    if (classPtr != IntPtr.Zero && IsReadablePointer(classPtr))
                    {
                        var cls = Marshal.PtrToStructure<NPClass>(classPtr);
                        if (cls.deallocate != IntPtr.Zero && IsExecutablePointer(cls.deallocate))
                        {
                            var dealloc =
                                Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NP_DeallocateDelegate>(
                                    cls.deallocate);
                            dealloc(objPtr);
                            return;
                        }
                    }

                    // Only free objects that we allocated.
                    bool owned;
                    lock (s_allocatedObjectPtrs)
                    {
                        owned = s_allocatedObjectPtrs.Remove(objPtr);
                    }

                    if (owned)
                        Marshal.FreeHGlobal(objPtr);

                    Logger.Log($"NPN_ReleaseObject after obj=0x{objPtr.ToString("x")}, freed={owned}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"NPN_ReleaseObject threw: {ex}");
                }
            });
            funcs.releaseobject = Marshal.GetFunctionPointerForDelegate(releaseObj);

            // NPN_GetPropertyProc
            var getPropertyStub = pin<NPAPIProcs.NPN_GetPropertyDelegate>(
                (IntPtr npp, IntPtr obj, IntPtr propertyName, IntPtr resultPtr) =>
                {
                    Logger.Log(
                        $"NPN_GetProperty obj={DescribeNPObjectRefCount(obj)}, property={DescribeNPIdentifier(propertyName)}, resultPtr=0x{resultPtr.ToString("x")}, resultBefore={DescribeNPVariantPtr(resultPtr)}");
                    if (resultPtr != IntPtr.Zero)
                    {
                        WriteVoidVariant(resultPtr);
                    }

                    Logger.Log(
                        $"NPN_GetProperty obj={DescribeNPObjectRefCount(obj)}, property={DescribeNPIdentifier(propertyName)}, resultPtr=0x{resultPtr.ToString("x")}, resultBefore={DescribeNPVariantPtr(resultPtr)}");

                    return false;
                });
            funcs.getproperty = Marshal.GetFunctionPointerForDelegate(getPropertyStub);

            // NPN_CreateObjectProc
            funcs.createobject = Marshal.GetFunctionPointerForDelegate(
                PinDelegate<NPN_CreateObjectDelegate>(NPN_CreateObject));

            // NPN_RetainObjectProc
            var retainObj = pin<NPAPIProcs.NPN_RetainObjectDelegate>((IntPtr objPtr) =>
            {

                try
                {
                    Logger.Log($"NPN_RetainObject before obj={DescribeNPObjectRefCount(objPtr)}");
                    if (objPtr == IntPtr.Zero) return IntPtr.Zero;
                    if (!IsReadablePointer(objPtr)) return objPtr;
                    var obj = Marshal.PtrToStructure<NPObject>(objPtr);
                    unchecked
                    {
                        obj.referenceCount++;
                    }

                    Marshal.StructureToPtr(obj, objPtr, false);
                    Logger.Log($"NPN_RetainObject after obj={DescribeNPObjectRefCount(objPtr)}");
                    return objPtr;
                }
                catch
                {
                    return objPtr;
                }
            });
            funcs.retainobject = Marshal.GetFunctionPointerForDelegate(retainObj);

            // NPN_ReleaseVariantValueProc (native runner: no-op)
            var releaseVariant = pin<NPAPIProcs.NPN_ReleaseVariantValueProcPtr>((IntPtr variantPtr) =>
            {
                Logger.Log(
                    $"NPN_ReleaseVariantValue variantPtr=0x{variantPtr:x}, value={DescribeNPVariantPtr(variantPtr)}");
            });
            funcs.releasevariantvalue = Marshal.GetFunctionPointerForDelegate(releaseVariant);

            // NPN_GetValueProc
            var getValueDel = new NPN_GetValueDelegate(NPN_GetValue);
            funcs.getvalue = Marshal.GetFunctionPointerForDelegate(getValueDel);

            // NPN_EvaluateProc
            var evaluateDel = pin<NPAPIProcs.NPN_EvaluateDelegate>(
                (IntPtr npp, IntPtr obj, IntPtr scriptPtr, IntPtr resultPtr) =>
                {
                    try
                    {
                        Logger.Log(
                            $"NPN_Evaluate obj={DescribeNPObjectRefCount(obj)}, scriptPtr=0x{scriptPtr:x}, resultPtr=0x{resultPtr:x}, resultBefore={DescribeNPVariantPtr(resultPtr)}");
                        string code = string.Empty;
                        if (scriptPtr != IntPtr.Zero && IsReadablePointer(scriptPtr))
                        {
                            try
                            {
                                var script = Marshal.PtrToStructure<NPString>(scriptPtr);
                                if (script.UTF8Characters != IntPtr.Zero && IsReadablePointer(script.UTF8Characters))
                                    code = Marshal.PtrToStringUTF8(script.UTF8Characters) ?? string.Empty;
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"NPN_Evaluate failed to read script string: {ex}");
                            }
                        }

                        Logger.Log(
                            $"NPN_Evaluate script='{code}', resultPtr=0x{resultPtr:x}, resultBefore={DescribeNPVariantPtr(resultPtr)}");

                        const string HOMEPAGE_CALLBACK_SCRIPT = "HomePage(\"UnityEngine.GameObject\");";
                        const string PAGEOUT_CALLBACK_SCRIPT = "PageOut(\"UnityEngine.GameObject\");";
                        const string AUTH_CALLBACK_SCRIPT = "authDoCallback(\"UnityEngine.GameObject\");";
                        const string NAVIGATE_SCRIPT = "location.href=\"";

                        if (code.StartsWith(HOMEPAGE_CALLBACK_SCRIPT, StringComparison.Ordinal)
                            || code.StartsWith(PAGEOUT_CALLBACK_SCRIPT, StringComparison.Ordinal))
                        {
                            // Application.Current?.Dispatcher?.BeginInvoke(new Action(() => Application.Current?.Shutdown()));
                            Application.Current?.Dispatcher?.BeginInvoke(new Action(() => PostQuitMessage(0)));

                        }
                        else if (code.StartsWith(AUTH_CALLBACK_SCRIPT, StringComparison.Ordinal))
                        {
                            string teg = TryGetArgValue("tegId", "TegId", "Username", "username") ?? string.Empty;
                            string auth = TryGetArgValue("authId", "AuthId", "token") ?? string.Empty;

                            Logger.Log(
                                $"NPN_Evaluate auth callback begin tid={Environment.CurrentManagedThreadId}, teg='{teg}', authPresent={!string.IsNullOrEmpty(auth)}");

                            if (!string.IsNullOrEmpty(teg) && !string.IsNullOrEmpty(auth))
                            {
                                Logger.Log($"Auto-auth as {teg}");

                                Logger.Log("NPN_Evaluate auth step -> SetTEGid");
                                UnitySendMessage("GlobalManager", "SetTEGid", MakeStringVariant(teg));
                                Logger.Log("NPN_Evaluate auth step <- SetTEGid");

                                Logger.Log("NPN_Evaluate auth step -> SetAuthid");
                                UnitySendMessage("GlobalManager", "SetAuthid", MakeStringVariant(auth));
                                Logger.Log("NPN_Evaluate auth step <- SetAuthid");

                                Logger.Log("NPN_Evaluate auth step -> DoAuth");
                                UnitySendMessage("GlobalManager", "DoAuth", MakeIntVariant(0));
                                Logger.Log("NPN_Evaluate auth step <- DoAuth");
                            }

                            Logger.Log($"NPN_Evaluate auth callback end tid={Environment.CurrentManagedThreadId}");
                        }

                        if (resultPtr != IntPtr.Zero)
                        {
                            var result = new NPVariant
                            {
                                type = NPVariantType.Void,
                                value = new NPVariant.NPVariantValue()
                            };

                            Marshal.StructureToPtr(result, resultPtr, false);
                        }

                        Logger.Log($"NPN_Evaluate completed resultAfter={DescribeNPVariantPtr(resultPtr)}");

                        return true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"NPN_Evaluate threw: {ex}");
                        return false;
                    }
                });
            funcs.evaluate = Marshal.GetFunctionPointerForDelegate(evaluateDel);

            // NPN_GetStringIdentifier
            var getStringId = pin<NPAPIProcs.NPN_GetStringIdentifierDelegate>((IntPtr namePtr) =>
            {
                Logger.Log($"NPN_GetStringIdentifier namePtr=0x{namePtr.ToString("x")}");
                if (namePtr == IntPtr.Zero) return IntPtr.Zero;
                string name = Marshal.PtrToStringUTF8(namePtr) ?? string.Empty;
                return NPIdentifierManager.GetStringIdentifier(name);
            });
            funcs.getstringidentifier = Marshal.GetFunctionPointerForDelegate(getStringId);

            // NPN_GetStringIdentifiers
            var getStringIds = pin<NPAPIProcs.NPN_GetStringIdentifiersDelegate>(
                (IntPtr namesPtr, int nameCount, IntPtr identifiersPtr) =>
                {
                    try
                    {
                        Logger.Log($"NPN_GetStringIdentifiers nameCount={nameCount}");
                        for (int i = 0; i < nameCount; i++)
                        {
                            IntPtr namePtr = Marshal.ReadIntPtr(namesPtr, i * IntPtr.Size);
                            string name = Marshal.PtrToStringUTF8(namePtr) ?? string.Empty;
                            IntPtr id = NPIdentifierManager.GetStringIdentifier(name);
                            Marshal.WriteIntPtr(identifiersPtr, i * IntPtr.Size, id);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"NPN_GetStringIdentifiers threw: {ex}");
                    }
                });
            funcs.getstringidentifiers = Marshal.GetFunctionPointerForDelegate(getStringIds);

        }

        public static class NPIdentifierManager
        {
            private static readonly Dictionary<string, IntPtr> _map = new();
            private static int _nextId = 1;

            public static IntPtr GetStringIdentifier(string name)
            {
                if (string.IsNullOrEmpty(name)) return IntPtr.Zero;

                if (!_map.TryGetValue(name, out var id))
                {
                    id = (IntPtr)_nextId++;
                    _map[name] = id;
                }

                return id;
            }
        }


        // Helper: create NPVariant string (tracks native allocation for later release)
        private static NPVariant MakeStringVariant(string s)
        {
            var v = new NPVariant();
            string text = s ?? string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            Marshal.WriteByte(p, bytes.Length, 0);

            lock (s_allocatedVariantStrings)
            {
                s_allocatedVariantStrings.Add(p);
            }

            v.type = NPVariantType.String;
            v.value.stringValue = new NPString
            {
                UTF8Characters = p,
                UTF8Length = (uint)bytes.Length
            };
            return v;
        }

        // Helper: create NPVariant int
        private static NPVariant MakeIntVariant(int i)
        {
            var v = new NPVariant
            {
                type = NPVariantType.Int32,
                value = new NPVariant.NPVariantValue() { intValue = i }
            };
            return v;
        }

        // Helper: attempts to read a string property from App.args by several common names
        private static string? TryGetArgValue(params string[] names)
        {
            try
            {
                Logger.Log($"TryGetArgValue trying names: {string.Join(", ", names)}");
                var argObj = App.Args;
                var t = argObj.GetType();
                foreach (var n in names)
                {
                    var pi = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                    if (pi == null) continue;
                    var val = pi.GetValue(argObj);
                    if (val is string s && !string.IsNullOrEmpty(s)) return s;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TryGetArgValue threw: {ex}");
            }

            return null;
        }

        private static void UnitySendMessage(string targetClass, string msg, NPVariant val)
        {
            Logger.Log(
                $"UnitySendMessage called tid={Environment.CurrentManagedThreadId}, target='{targetClass}', msg='{msg}', val={DescribeNPVariant(val)}");
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.BeginInvoke(() =>
                    {
                        Logger.Log(
                            $"UnitySendMessage exit tid={Environment.CurrentManagedThreadId}, target='{targetClass}', msg='{msg}'");
                        UnitySendMessageInternal(targetClass, msg, val);
                    },
                    System.Windows.Threading.DispatcherPriority.Send);
            }
            else
            {
                UnitySendMessageInternal(targetClass, msg, val);
            }

        }

        private static void UnitySendMessageInternal(string targetClass, string msg, NPVariant val)
        {
            try
            {
                Logger.Log(
                    $"UnitySendMessageInternal entered tid={Environment.CurrentManagedThreadId}, target='{targetClass}', msg='{msg}', val={DescribeNPVariant(val)}");
                IntPtr scriptable = PluginBootstrap.scriptableObject;
                if (scriptable == IntPtr.Zero || !IsReadablePointer(scriptable))
                {
                    Logger.Log("UnitySendMessage: scriptableObject invalid");
                    return;
                }

                // Ensure _class->_invoke is valid
                if (s_scriptableClassPtr == IntPtr.Zero || !IsReadablePointer(s_scriptableClassPtr))
                {
                    Logger.Log("UnitySendMessage: classPtr invalid");
                    return;
                }

                var npclass = Marshal.PtrToStructure<NPClass>(s_scriptableClassPtr);
                if (!IsExecutablePointer(npclass.invoke))
                {
                    Logger.Log("UnitySendMessage: NPClass _invoke not executable");
                    return;
                }

                IntPtr sendId = NPIdentifierManager.GetStringIdentifier("SendMessage");
                if (sendId == IntPtr.Zero)
                {
                    Logger.Log("UnitySendMessage: SendMessage identifier invalid");
                    return;
                }


                // --- PIN NPVariant strings to prevent GC/memory corruption ---
                GCHandle handleTarget = GCHandle.Alloc(targetClass, GCHandleType.Pinned);
                GCHandle handleMsg = GCHandle.Alloc(msg, GCHandleType.Pinned);

                NPVariant[] argsManaged = new NPVariant[3];
                argsManaged[0] = MakeStringVariant(targetClass); // Already unmanaged pinned
                argsManaged[1] = MakeStringVariant(msg);
                argsManaged[2] = val;

                int variantSize = Marshal.SizeOf<NPVariant>();
                IntPtr argsPtr = Marshal.AllocHGlobal(variantSize * argsManaged.Length);

                try
                {
                    for (int i = 0; i < argsManaged.Length; i++)
                    {
                        Marshal.StructureToPtr(argsManaged[i], IntPtr.Add(argsPtr, i * variantSize), false);
                    }

                    var invokeDel = Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NP_InvokeDelegate>(npclass.invoke);

                    var result = new NPVariant
                    {
                        type = NPVariantType.Void,
                        value = new NPVariant.NPVariantValue()
                    };
                    var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NPVariant>());
                    Marshal.StructureToPtr(result, resultPtr, false);
                    bool ok = invokeDel(scriptable, sendId, argsPtr, (uint)argsManaged.Length, resultPtr);
                    Logger.Log($"UnitySendMessage invoke ok={ok}, result={DescribeNPVariantPtr(resultPtr)}");
                }
                finally
                {
                    //Marshal.FreeHGlobal(argsPtr);
                    //handleTarget.Free();
                    //handleMsg.Free();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UnitySendMessage threw: {ex}");
            }
        }

        // Call during application/plugin shutdown to free native allocations held by stubs
        public static void Cleanup()
        {
            Logger.Log("NPAPIStubs.Cleanup entered");
            try
            {
                // Free identifier strings
                lock (s_allocatedIdentifierPtrs)
                {
                    foreach (var p in s_allocatedIdentifierPtrs.Where(p => p != IntPtr.Zero))
                    {
                        Marshal.FreeHGlobal(p);
                    }

                    s_allocatedIdentifierPtrs.Clear();
                }

                // Free created NPObjects (single owner list; do NOT free s_locationObjectPtrs separately)
                lock (s_allocatedObjectPtrs)
                {
                    foreach (var p in s_allocatedObjectPtrs.Where(p => p != IntPtr.Zero))
                    {
                        Marshal.FreeHGlobal(p);
                    }

                    s_allocatedObjectPtrs.Clear();
                }

                // Location objects are a tracking subset; just clear tracking
                lock (s_locationObjectPtrs)
                {
                    s_locationObjectPtrs.Clear();
                }

                // Free variant strings we allocated
                lock (s_allocatedVariantStrings)
                {
                    foreach (var p in s_allocatedVariantStrings.Where(p => p != IntPtr.Zero))
                    {
                        Marshal.FreeHGlobal(p);
                    }

                    s_allocatedVariantStrings.Clear();
                }

                // Free browser object/class persistent pointers
                if (s_browserObjectPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(s_browserObjectPtr);
                    s_browserObjectPtr = IntPtr.Zero;
                }

                if (s_browserClassPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(s_browserClassPtr);
                    s_browserClassPtr = IntPtr.Zero;
                }

                if (userAgentHandle.IsAllocated)
                    userAgentHandle.Free();

                pinnedDelegates.Clear();
                Logger.Log("NPAPIStubs.Cleanup completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"NPAPIStubs.Cleanup threw: {ex}");
            }
        }


        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer,
            IntPtr dwLength);

        private static IntPtr s_scriptableClassPtr = IntPtr.Zero;

        private static string ReadAnsiString(IntPtr ptr, int maxBytes = 4096)
        {
            Logger.Log($"ReadAnsiString: ptr=0x{ptr.ToString("x")}, maxBytes={maxBytes}");
            if (ptr == IntPtr.Zero || !IsReadablePointer(ptr))
                return string.Empty;

            var bytes = new List<byte>(Math.Min(maxBytes, 256));
            for (int i = 0; i < maxBytes; i++)
            {
                IntPtr current = IntPtr.Add(ptr, i);
                if (!IsReadablePointer(current))
                    break;

                byte b = Marshal.ReadByte(current);
                if (b == 0)
                    break;

                bytes.Add(b);
            }

            return bytes.Count == 0 ? string.Empty : Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static bool IsReadablePointer(IntPtr ptr)
        {
            Logger.Log($"IsReadablePointer: ptr=0x{ptr.ToString("x")}");
            if (ptr == IntPtr.Zero) return false;
            if (VirtualQuery(ptr, out var mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
                return false;

            const uint MEM_COMMIT = 0x1000;
            const uint PAGE_NOACCESS = 0x01;
            const uint PAGE_GUARD = 0x100;

            if (mbi.State != MEM_COMMIT) return false;
            if ((mbi.Protect & PAGE_NOACCESS) != 0) return false;
            if ((mbi.Protect & PAGE_GUARD) != 0) return false;

            return true;
        }

        private static bool IsExecutablePointer(IntPtr ptr)
        {
            Logger.Log($"IsExecutablePointer checking 0x{ptr.ToString("x")}");
            if (ptr == IntPtr.Zero) return false;
            if (VirtualQuery(ptr, out var mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
                return false;

            const uint MEM_COMMIT = 0x1000;
            const uint PAGE_EXECUTE = 0x10;
            const uint PAGE_EXECUTE_READ = 0x20;
            const uint PAGE_EXECUTE_READWRITE = 0x40;
            const uint PAGE_EXECUTE_WRITECOPY = 0x80;

            if (mbi.State != MEM_COMMIT) return false;

            uint execMask = PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
            return (mbi.Protect & execMask) != 0;
        }

        private static IntPtr TryFindClassPtr(IntPtr root)
        {
            return TryFindClassPtrInternal(root, 0);
        }

        private static IntPtr TryFindClassPtrInternal(IntPtr ptr, int depth)
        {
            Logger.Log($"TryFindClassPtrInternal: depth={depth}, ptr=0x{ptr.ToString("x")}");
            if (depth > 2 || !IsReadablePointer(ptr))
                return IntPtr.Zero;

            for (int i = 0; i < 4; i++)
            {
                IntPtr candidate = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                if (!IsReadablePointer(candidate)) continue;

                int version = Marshal.ReadInt32(candidate);
                Logger.Verbose(
                    $"TryFindClassPtr: depth={depth}, offset={i * IntPtr.Size}, candidate=0x{candidate.ToString("x")}, version={version}");

                // Normal NPClass path
                if (version == 3 || version == 2)
                    return candidate;

                // Heuristic: hasMethod + invoke look executable
                IntPtr
                    hasMethod = Marshal.ReadIntPtr(candidate,
                        16); // structVersion(4) + allocate(4) + deallocate(4) + invalidate(4)
                IntPtr invoke = Marshal.ReadIntPtr(candidate, 20);
                if (IsExecutablePointer(hasMethod) && IsExecutablePointer(invoke))
                {
                    Logger.Verbose($"TryFindClassPtr: heuristic match at 0x{candidate.ToString("x")}");
                    return candidate;
                }

                IntPtr nested = TryFindClassPtrInternal(candidate, depth + 1);
                if (nested != IntPtr.Zero)
                    return nested;
            }

            return IntPtr.Zero;
        }

        public static void WarmUpScriptableObject(IntPtr scriptable)
        {
            if (scriptable == IntPtr.Zero)
            {
                Logger.Log("WarmUpScriptableObject: scriptable is NULL");
                return;
            }

            try
            {
                if (!IsReadablePointer(scriptable))
                {
                    Logger.Log($"WarmUpScriptableObject: scriptable not readable: 0x{scriptable.ToString("x")}");
                    return;
                }

                var npobj = Marshal.PtrToStructure<NPObject>(scriptable);
                if (npobj._class == IntPtr.Zero || !IsReadablePointer(npobj._class))
                {
                    Logger.Log("WarmUpScriptableObject: npobj._class is NULL or unreadable");
                    return;
                }

                var cls = Marshal.PtrToStructure<NPClass>(npobj._class);
                if (cls.hasMethod == IntPtr.Zero || !IsExecutablePointer(cls.hasMethod))
                {
                    Logger.Log("WarmUpScriptableObject: class._hasMethod is NULL or not executable");
                    return;
                }

                IntPtr styleId = NPIdentifierManager.GetStringIdentifier("style");
                if (styleId == IntPtr.Zero)
                {
                    Logger.Log("WarmUpScriptableObject: style identifier is NULL");
                    return;
                }

                var hasMethod = Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NP_HasMethodDelegate>(cls.hasMethod);
                bool ok = hasMethod(scriptable, styleId);
                Logger.Log($"WarmUpScriptableObject: hasMethod('style') returned {ok}");
            }
            catch (Exception ex)
            {
                Logger.Log($"WarmUpScriptableObject threw: {ex}");
            }
        }

        public static void InitializeScriptableObject(IntPtr scriptable)
        {
            Logger.Log($"InitializeScriptableObject: scriptable=0x{scriptable.ToString("x")}");

            if (!IsReadablePointer(scriptable))
            {
                Logger.Log("InitializeScriptableObject: scriptable pointer not readable");
                return;
            }

            try
            {
                var npobj = Marshal.PtrToStructure<NPObject>(scriptable);
                if (npobj._class != IntPtr.Zero && IsReadablePointer(npobj._class))
                {
                    s_scriptableClassPtr = npobj._class;
                    Logger.Log($"InitializeScriptableObject: NPObject._class=0x{s_scriptableClassPtr.ToString("x")}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"InitializeScriptableObject: reading NPObject failed: {ex}");
            }

            // Fallback: heuristics (in case the pointer isn't a direct NPObject*)
            if (s_scriptableClassPtr == IntPtr.Zero)
                s_scriptableClassPtr = TryFindClassPtr(scriptable);

            // Fallback: maybe we got a pointer-to-pointer
            if (s_scriptableClassPtr == IntPtr.Zero)
            {
                IntPtr deref = Marshal.ReadIntPtr(scriptable);
                Logger.Log($"InitializeScriptableObject: deref=0x{deref:x}");
                if (IsReadablePointer(deref))
                    s_scriptableClassPtr = TryFindClassPtr(deref);
            }

            if (s_scriptableClassPtr == IntPtr.Zero)
            {
                Logger.Log("InitializeScriptableObject: could not resolve NPClass");
                return;
            }

            Logger.Log($"InitializeScriptableObject: classPtr=0x{s_scriptableClassPtr.ToString("x")}");
        }
    }
}