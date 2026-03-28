using System.Diagnostics;
using System.Formats.Asn1;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using static ffrunner.NPAPIProcs;
using static ffrunner.Structs;
using static ffrunner.Structs.NPVariant;
using NPClass = ffrunner.Structs.NPClass;
using NPObject = ffrunner.Structs.NPObject;
using static ffrunner.StubHelper;
namespace ffrunner
{
    public static class NPAPIStubs
    {
        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        // Keep delegates alive to prevent GC collection for plugin lifetime
        public static readonly List<Delegate> pinnedDelegates = new();


        // Exposed plugin delegate instances (marshaled from plugin function pointers)
        public static NPAPIProcs.NPP_New_Unmanaged_Cdecl Plugin_New;
        public static NPAPIProcs.NPP_Destroy_Unmanaged_Cdecl? Plugin_Destroy;
        public static NPAPIProcs.NPP_SetWindow_Unmanaged_Cdecl? Plugin_SetWindow;
        public static NPAPIProcs.NPP_GetValue_Unmanaged_Cdecl? Plugin_GetValue;
        public static NPAPIProcs.NPP_URLNotifyDelegate? Plugin_UrlNotify;

        // Streaming plugin functions
        public static NPAPIProcs.NPP_NewStream_Unmanaged_Cdecl? Plugin_NewStream;
        public static NPAPIProcs.NPP_DestroyStream_Unmanaged_Cdecl? Plugin_DestroyStream;
        public static NPAPIProcs.NPP_WriteReady_Unmanaged_Cdecl? Plugin_WriteReady;
        public static NPAPIProcs.NPP_Write_Unmanaged_Cdecl? Plugin_Write;

        // NPObject / NPClass for browser
        public static NPObject browserObject;

        // Unmanaged pointers used by stubs and returned to plugin (must persist)
        public static IntPtr s_browserClassPtr = IntPtr.Zero;
        public static IntPtr s_browserObjectPtr = IntPtr.Zero;
        public static IntPtr s_locationObjectPtr = IntPtr.Zero;
        private static string s_locationHref = string.Empty;
        public static List<IntPtr> s_stringAllocs = new List<IntPtr>();
        // Identifier interning storage
        public static readonly Dictionary<string, IntPtr> s_identifierMap = new(StringComparer.Ordinal);
        public static readonly List<IntPtr> s_allocatedIdentifierPtrs = new();
        public static readonly List<IntPtr> s_allocatedObjectPtrs = new();

        // Track location objects and variant string allocations
        public static readonly List<IntPtr> s_locationObjectPtrs = new();
        public static readonly List<IntPtr> s_allocatedVariantStrings = new();

        // Pinned user agent bytes and delegate
        private static readonly byte[] userAgentBytes = Encoding.ASCII.GetBytes("ffrunner.exe\0");
        private static GCHandle userAgentHandle = GCHandle.Alloc(userAgentBytes, GCHandleType.Pinned);
        private static IntPtr userAgentPtr = IntPtr.Zero;
        private static NPAPIProcs.NPN_UserAgentDelegate? uagentDel;
        public static IntPtr s_browserWindowHandle = IntPtr.Zero;

        // Pin helper
        private static T PinDelegate<T>(T del) where T : Delegate
        {
            pinnedDelegates.Add(del);
            return del;
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
                try
                {
                    Logger.Log(
                        $"NP_Allocate instance=0x{instancePtr.ToString("x")}, class=0x{aClassPtr.ToString("x")}");
                    var npobj = new NPObject
                    {
                        _class = aClassPtr,
                        referenceCount = 1,
                    };

                    var p = Marshal.AllocHGlobal(Marshal.SizeOf<NPObject>());
                    Marshal.StructureToPtr(npobj, p, false);

                    lock (s_allocatedObjectPtrs)
                    {
                        s_allocatedObjectPtrs.Add(p);
                    }

                    return p;
                }
                catch (Exception ex)
                {
                    Logger.Log($"NP_Allocate threw: {ex}");
                    return IntPtr.Zero;
                }


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
                try
                {
                    Logger.Log($"NP_HasProperty obj=0x{npobjPtr.ToString("x")}, name=0x{name.ToString("x")}");

                    if (npobjPtr == s_browserObjectPtr && IdentifierEqualsString(name, "location"))
                        return true;

                    if (IsLocationObject(npobjPtr) && IdentifierEqualsString(name, "href"))
                        return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"NP_HasProperty threw: {ex}");
                }

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
                    try
                    {
                        Logger.Log($"NP_GetProperty obj=0x{npobj:x}, name=0x{name:x}");

                        // window.location
                        if (npobj == s_browserObjectPtr && IdentifierEqualsString(name, "location"))
                        {
                            SetNPVariantObject(resultPtr, s_locationObjectPtr);
                            return true;
                        }

                        // location.href
                        if (npobj == s_locationObjectPtr && IdentifierEqualsString(name, "href"))
                        {
                            string url = App.Args.MainPathOrAddress ?? "";
                            
                            SetNPVariantString(resultPtr, url);

                            return true;
                        }

                        // IMPORTANT: explicitly set VOID
                        SetNPVariantVoid(resultPtr);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"NP_GetProperty threw: {ex}");
                        SetNPVariantVoid(resultPtr);
                        return false;
                    }
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
                    try
                    {
                        Logger.Log(
                            $"NP_Construct obj=0x{npobj:x}, argc={argCount}, args={DescribeNPVariantsBuffer(argsPtr, argCount)}, resultPtr=0x{resultPtr:x}, resultBefore={DescribeNPVariantPtr(resultPtr)}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"NP_Construct threw: {ex}");
                    }

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

            // Create location object (same class, like ffrunner.c)
            var locationObj = new NPObject
            {
                _class = s_browserClassPtr,
                referenceCount = 1,
            };

            s_locationObjectPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NPObject>());
            Marshal.StructureToPtr(locationObj, s_locationObjectPtr, false);
        }

        public static void InitPluginDelegates(ref NPPluginFuncs funcs)
        {

            Logger.Log("InitPluginDelegates entered");
            if (funcs.newp != IntPtr.Zero)
            {
                Plugin_New =
                    Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NPP_New_Unmanaged_Cdecl>(funcs.newp);
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
                pinnedDelegates.Add(Plugin_UrlNotify);
;
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
            try
            {
                Logger.Log($"InitNetscapeFuncs entered size={funcs.size}, version={funcs.version}");

                // Helper to pin and return delegates
                T pin<T>(T d) where T : Delegate
                {
                    pinnedDelegates.Add(d);
                    return d;
                }

                // NPN_GetURLProc
                var geturlDel = pin<NPAPIProcs.NPN_GetURLDelegate>((IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string url, [MarshalAs(UnmanagedType.LPTStr)]string window) =>
                {
                    try
                    {

                        Logger.Log($"NPN_GetURL url='{url}', window='{window}'");
                        Network.RegisterGetRequest(url, false, IntPtr.Zero);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"NPN_GetURL threw: {ex}");
                    }

                    return 0; // NPERR_NO_ERROR
                });
                funcs.geturl = Marshal.GetFunctionPointerForDelegate(geturlDel);

                // NPN_PostURLProc
                var posturlDel = pin<NPAPIProcs.NPN_PostURLDelegate>(
                    (IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string url,
                        [MarshalAs(UnmanagedType.LPStr)] string window, uint len, [MarshalAs(UnmanagedType.LPStr)] string buf,
                        [MarshalAs(UnmanagedType.I1)] bool file) =>
                    {
                        try
                        {

                            Logger.Log(
                                $"NPN_PostURL url='{url}', window='{window}', len={len}, file={file}, buf='{buf}'");


                            Network.RegisterPostRequest(url, false, IntPtr.Zero, buf, len);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"NPN_PostURL threw: {ex}");
                        }

                        return 0; // NPERR_NO_ERROR
                    });
                funcs.posturl = Marshal.GetFunctionPointerForDelegate(posturlDel);

                // NPN_UserAgentProc
                userAgentPtr = userAgentHandle.AddrOfPinnedObject();
                uagentDel = PinDelegate<NPAPIProcs.NPN_UserAgentDelegate>((IntPtr instance) =>
                {
                    Logger.Log("NPN_UserAgent called, returning 'ffrunner'");
                    return "ffrunner.exe"; // CLR marshals this back as const char*
                });

                funcs.uagent = Marshal.GetFunctionPointerForDelegate(uagentDel);
                // Prepare user agent delegate and pointer


                // NPN_GetURLNotifyProc
                var geturlNotifyDel = pin<NPAPIProcs.NPN_GetURLNotifyDelegate>(
                    (IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string url,
                        [MarshalAs(UnmanagedType.LPStr)] string window, IntPtr notifyData) =>
                    {
                        try
                        {
                            Logger.Log(
                                $"NPN_GetURLNotify url='{url}', window='{window}', notifyData=0x{notifyData.ToString("x")}");

                            // Just enqueue the request with notifyData
                            Network.RegisterGetRequest(url, true, notifyData);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"NPN_GetURLNotify threw: {ex}");
                        }

                        return 0; // NPERR_NO_ERROR
                    });
                funcs.geturlnotify = Marshal.GetFunctionPointerForDelegate(geturlNotifyDel);

                var invokeStub = pin<NPAPIProcs.NPN_InvokeDelegate>(
                    (IntPtr npp, IntPtr obj, IntPtr methodName, IntPtr argsPtr, uint argCount, IntPtr resultPtr) =>
                    {
                        try
                        {
                            Logger.Log(
                                $"NPN_Invoke obj={DescribeNPObjectRefCount(obj)}, method={DescribeNPIdentifier(methodName)}, argc={argCount}, args={DescribeNPVariantsBuffer(argsPtr, argCount)}, resultPtr=0x{resultPtr.ToString("x")}, resultBefore={DescribeNPVariantPtr(resultPtr)}");
                            WriteVoidVariant(resultPtr);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"NPN_Invoke threw: {ex}");
                        }

                        return false;
                    });
                funcs.invoke = Marshal.GetFunctionPointerForDelegate(invokeStub);
                // NPN_PostURLNotifyProc
                var posturlNotifyDel = pin<NPAPIProcs.NPN_PostURLNotifyDelegate>(
                    (IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string url,
                        [MarshalAs(UnmanagedType.LPStr)] string window, uint len, [MarshalAs(UnmanagedType.LPStr)] string buf,
                        [MarshalAs(UnmanagedType.I1)] bool file, IntPtr notifyData) =>
                    {
                        try
                        {
                            Logger.Log(
                                $"NPN_PostURLNotify url='{url}', window='{window}', len={len}, file={file}, notifyData=0x{notifyData.ToString("x")}, buf='{buf}'");


                            Network.RegisterPostRequest(url, true, notifyData, buf, len);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"NPN_PostURLNotify threw: {ex}");
                        }

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
                        var classPtr = obj._class;
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
                        try
                        {
                            Logger.Log(
                                $"NPN_GetProperty obj={DescribeNPObjectRefCount(obj)}, property={DescribeNPIdentifier(propertyName)}, resultPtr=0x{resultPtr.ToString("x")}, resultBefore={DescribeNPVariantPtr(resultPtr)}");
                            if (resultPtr != IntPtr.Zero)
                            {
                                WriteVoidVariant(resultPtr);
                            }

                            Logger.Log(
                                $"NPN_GetProperty obj={DescribeNPObjectRefCount(obj)}, property={DescribeNPIdentifier(propertyName)}, resultPtr=0x{resultPtr.ToString("x")}, resultBefore={DescribeNPVariantPtr(resultPtr)}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"NPN_GetProperty threw: {ex}");
                        }

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
                    try
                    {
                        if (variantPtr == IntPtr.Zero) return;

                        var variant = Marshal.PtrToStructure<NPVariant>(variantPtr);

                        if (variant.type == NPVariantType.String)
                        {
                            var str = variant.value.stringValue;
                            if (str.UTF8Characters != IntPtr.Zero)
                            {
                                Marshal.FreeHGlobal(str.UTF8Characters);
                            }
                        }

                        variant.type = NPVariantType.Void;
                        Marshal.StructureToPtr(variant, variantPtr, false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"NPN_ReleaseVariantValue threw: {ex}");
                    }
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
                            var code = string.Empty;
                            if (scriptPtr != IntPtr.Zero && IsReadablePointer(scriptPtr))
                            {
                                try
                                {
                                    var script = Marshal.PtrToStructure<NPString>(scriptPtr);
                                    if (script.UTF8Characters != IntPtr.Zero &&
                                        IsReadablePointer(script.UTF8Characters))
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
                                var teg = TryGetArgValue("tegId", "TegId", "Username", "username") ?? string.Empty;
                                var auth = TryGetArgValue("authId", "AuthId", "token") ?? string.Empty;

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
                    var name = Marshal.PtrToStringUTF8(namePtr) ?? string.Empty;
                    return GetNPIdentifier(name);
                });
                funcs.getstringidentifier = Marshal.GetFunctionPointerForDelegate(getStringId);

                // NPN_GetStringIdentifiers
                var getStringIds = pin<NPAPIProcs.NPN_GetStringIdentifiersDelegate>(
                    (IntPtr namesPtr, int nameCount, IntPtr identifiersPtr) =>
                    {
                        try
                        {
                            Logger.Log($"NPN_GetStringIdentifiers nameCount={nameCount}");
                            for (var i = 0; i < nameCount; i++)
                            {
                                var namePtr = Marshal.ReadIntPtr(namesPtr, i * IntPtr.Size);
                                var name = Marshal.PtrToStringUTF8(namePtr) ?? string.Empty;
                                var id = GetNPIdentifier(name);
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
            catch (Exception ex)
            {
                Logger.Log($"InitNetscapeFuncs threw: {ex}");
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


    }
}