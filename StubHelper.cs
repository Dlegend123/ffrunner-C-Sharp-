 using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
 using System.Windows;
 using static ffrunner.NPAPIStubs;
using static ffrunner.Structs;
using static ffrunner.Structs.NPVariant;

namespace ffrunner
{
    public class StubHelper
    {

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

        public static string ReadAnsiString(IntPtr ptr, int maxBytes = 4096)
        {
            try
            {
                Logger.Log($"ReadAnsiString: ptr=0x{ptr.ToString("x")}, maxBytes={maxBytes}");
                if (ptr == IntPtr.Zero || !IsReadablePointer(ptr))
                    return string.Empty;

                var bytes = new List<byte>(Math.Min(maxBytes, 256));
                for (var i = 0; i < maxBytes; i++)
                {
                    var current = IntPtr.Add(ptr, i);
                    if (!IsReadablePointer(current))
                        break;

                    var b = Marshal.ReadByte(current);
                    if (b == 0)
                        break;

                    bytes.Add(b);
                }

                return bytes.Count == 0 ? string.Empty : Encoding.ASCII.GetString(bytes.ToArray());
            }
            catch (Exception e)
            {
                Logger.Log($"ReadAnsiString threw: {e}");
                return string.Empty;
            }

        }

        public static bool IsReadablePointer(IntPtr ptr)
        {
            try
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
            catch (Exception ex)
            {
                Logger.Log($"IsReadablePointer threw: {ex}");
                return false;
            }
        }

        public static bool IsExecutablePointer(IntPtr ptr)
        {
            try
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

                var execMask = PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
                return (mbi.Protect & execMask) != 0;
            }
            catch (Exception ex)
            {
                Logger.Log($"IsExecutablePointer threw: {ex}");
                return false;
            }
        }

        private static IntPtr TryFindClassPtr(IntPtr root)
        {
            return TryFindClassPtrInternal(root, 0);
        }

        private static IntPtr TryFindClassPtrInternal(IntPtr ptr, int depth)
        {
            try
            {
                Logger.Log($"TryFindClassPtrInternal: depth={depth}, ptr=0x{ptr.ToString("x")}");
                if (depth > 2 || !IsReadablePointer(ptr))
                    return IntPtr.Zero;

                for (var i = 0; i < 4; i++)
                {
                    var candidate = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                    if (!IsReadablePointer(candidate)) continue;

                    var version = Marshal.ReadInt32(candidate);
                    Logger.Verbose(
                        $"TryFindClassPtr: depth={depth}, offset={i * IntPtr.Size}, candidate=0x{candidate.ToString("x")}, version={version}");

                    // Normal NPClass path
                    if (version == 3 || version == 2)
                        return candidate;

                    // Heuristic: hasMethod + invoke look executable
                    var
                        hasMethod = Marshal.ReadIntPtr(candidate,
                            16); // structVersion(4) + allocate(4) + deallocate(4) + invalidate(4)
                    var invoke = Marshal.ReadIntPtr(candidate, 20);
                    if (IsExecutablePointer(hasMethod) && IsExecutablePointer(invoke))
                    {
                        Logger.Verbose($"TryFindClassPtr: heuristic match at 0x{candidate.ToString("x")}");
                        return candidate;
                    }

                    var nested = TryFindClassPtrInternal(candidate, depth + 1);
                    if (nested != IntPtr.Zero)
                        return nested;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TryFindClassPtrInternal threw at depth={depth}, ptr=0x{ptr.ToString("x")}: {ex}");
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

                var styleId = GetNPIdentifier("style");
                if (styleId == IntPtr.Zero)
                {
                    Logger.Log("WarmUpScriptableObject: style identifier is NULL");
                    return;
                }

                var hasMethod = Marshal.GetDelegateForFunctionPointer<NPAPIProcs.NP_HasMethodDelegate>(cls.hasMethod);
                var ok = hasMethod(scriptable, styleId);
                Logger.Log($"WarmUpScriptableObject: hasMethod('style') returned {ok}");
            }
            catch (Exception ex)
            {
                Logger.Log($"WarmUpScriptableObject threw: {ex}");
            }
        }
        public static IntPtr GetNPIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return IntPtr.Zero;

            lock (s_identifierMap)
            {
                if (s_identifierMap.TryGetValue(name, out var ptr))
                    return ptr;

                byte[] bytes = Encoding.ASCII.GetBytes(name + '\0');
                IntPtr alloc = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, alloc, bytes.Length);

                s_identifierMap[name] = alloc;
                s_allocatedIdentifierPtrs.Add(alloc);

                Logger.Log($"GetNPIdentifier created '{name}' ptr=0x{alloc.ToString("x")}");
                return alloc;
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

            try
            {
                // Fallback: heuristics (in case the pointer isn't a direct NPObject*)
                if (s_scriptableClassPtr == IntPtr.Zero)
                    s_scriptableClassPtr = TryFindClassPtr(scriptable);

                // Fallback: maybe we got a pointer-to-pointer
                if (s_scriptableClassPtr == IntPtr.Zero)
                {
                    var deref = Marshal.ReadIntPtr(scriptable);
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
            catch (Exception ex)
            {
                Logger.Log($"InitializeScriptableObject: resolving NPClass failed: {ex}");
            }
        }


        public static class NPIdentifierManager
        {
            private static readonly Dictionary<string, IntPtr> _map = new();

            public static IntPtr GetStringIdentifier(string name)
            {
                if (string.IsNullOrEmpty(name))
                    return IntPtr.Zero;

                if (!_map.TryGetValue(name, out var ptr))
                {
                    var bytes = Encoding.ASCII.GetBytes(name + "\0");
                    ptr = Marshal.AllocHGlobal(bytes.Length);
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);

                    _map[name] = ptr;
                }

                return ptr; // ✅ REAL POINTER
            }
        }

        // Helper: create NPVariant string (tracks native allocation for later release)
        public static NPVariant MakeStringVariant(string s)
        {
            var v = new NPVariant();
            var text = s ?? string.Empty;
            var bytes = Encoding.UTF8.GetBytes(text);
            var p = Marshal.AllocHGlobal(bytes.Length + 1);
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
        public static NPVariant MakeIntVariant(int i)
        {
            var v = new NPVariant
            {
                type = NPVariantType.Int32,
                value = new NPVariant.NPVariantValue() { intValue = i }
            };
            return v;
        }

        // Helper: attempts to read a string property from App.args by several common names
        public static string? TryGetArgValue(params string[] names)
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

        public static void UnitySendMessage(string targetClass, string msg, NPVariant val)
        {
            try
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
            catch (Exception ex)
            {
                Logger.Log($"UnitySendMessage threw: {ex}");
            }

        }

        private static void UnitySendMessageInternal(string targetClass, string msg, NPVariant val)
        {
            try
            {
                Logger.Log(
                    $"UnitySendMessageInternal entered tid={Environment.CurrentManagedThreadId}, target='{targetClass}', msg='{msg}', val={DescribeNPVariant(val)}");
                var scriptable = PluginBootstrap.scriptableObject;
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

                var sendId = GetNPIdentifier("SendMessage");
                if (sendId == IntPtr.Zero)
                {
                    Logger.Log("UnitySendMessage: SendMessage identifier invalid");
                    return;
                }


                // --- PIN NPVariant strings to prevent GC/memory corruption ---
                var handleTarget = GCHandle.Alloc(targetClass, GCHandleType.Pinned);
                var handleMsg = GCHandle.Alloc(msg, GCHandleType.Pinned);

                var argsManaged = new NPVariant[3];
                argsManaged[0] = MakeStringVariant(targetClass); // Already unmanaged pinned
                argsManaged[1] = MakeStringVariant(msg);
                argsManaged[2] = val;

                var variantSize = Marshal.SizeOf<NPVariant>();
                var argsPtr = Marshal.AllocHGlobal(variantSize * argsManaged.Length);

                try
                {
                    for (var i = 0; i < argsManaged.Length; i++)
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
                    var ok = invokeDel(scriptable, sendId, argsPtr, (uint)argsManaged.Length, resultPtr);
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


        private static string DescribeNPString(Structs.NPString value)
        {
            try
            {
                if (value.UTF8Characters == IntPtr.Zero)
                    return "(null)";

                if (!IsReadablePointer(value.UTF8Characters))
                    return $"<unreadable:0x{value.UTF8Characters:x}>";

                var length = checked((int)Math.Min(value.UTF8Length, 512u));
                var bytes = new byte[length];
                if (length > 0)
                    Marshal.Copy(value.UTF8Characters, bytes, 0, length);

                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                return $"<npstring-error:{ex.GetType().Name}>";
            }
        }

        public static string DescribeNPIdentifier(IntPtr identifier)
        {
            if (identifier == IntPtr.Zero)
                return "(null)";

            try
            {
                var isInt = (identifier & 1) == 1;
                if (isInt)
                    return $"int:{(((nint)identifier) >> 1)}";

                if (!IsReadablePointer(identifier))
                    return $"ptr:0x{identifier:x}";

                var text = ReadAnsiString(identifier, 128);
                return string.IsNullOrEmpty(text) ? $"ptr:0x{identifier.ToString("x")}" : $"str:'{text}'";
            }
            catch (Exception ex)
            {
                return $"<identifier-error:{ex.GetType().Name}>";
            }
        }

        public static string DescribeNPObjectRefCount(IntPtr objPtr)
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

        public static void WriteVoidVariant(IntPtr resultPtr)
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

        public static bool IdentifierEqualsString(IntPtr identifier, string expected)
        {
            if (identifier == IntPtr.Zero)
                return false;

            if ((((nint)identifier) & 1) == 1)
                return false;

            if (!IsReadablePointer(identifier))
                return false;

            var actual = ReadAnsiString(identifier, 128);
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }

        public static bool IsLocationObject(IntPtr objPtr)
        {
            try
            {
                if (objPtr == IntPtr.Zero)
                    return false;

                lock (s_locationObjectPtrs)
                {
                    return s_locationObjectPtrs.Contains(objPtr);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"IsLocationObject threw: {ex}");
                return false;
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

        public static string DescribeNPVariantPtr(IntPtr variantPtr)
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

        public static string DescribeNPVariantsBuffer(IntPtr argsPtr, uint argCount)
        {
            if (argsPtr == IntPtr.Zero || argCount == 0)
                return "[]";

            try
            {
                var size = Marshal.SizeOf<Structs.NPVariant>();
                string[] parts = new string[argCount];
                for (var i = 0; i < argCount; i++)
                {
                    var current = IntPtr.Add(argsPtr, i * size);
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

            Logger.Log(
                $"NPN_GetValue returning obj=0x{s_browserObjectPtr.ToInt64():x} refCount={browserObject.referenceCount}");
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
            var p = Marshal.AllocHGlobal(Marshal.SizeOf<NPObject>());
            Marshal.StructureToPtr(npobj, p, false);

            lock (s_allocatedObjectPtrs)
            {
                s_allocatedObjectPtrs.Add(p);
            }

            return p;
        }

        public static void SetNPVariantVoid(IntPtr variantPtr)
        {
            var v = new NPVariant
            {
                type = NPVariantType.Void
            };
            Marshal.StructureToPtr(v, variantPtr, false);
        }

        public static void SetNPVariantObject(IntPtr variantPtr, IntPtr objPtr)
        {
            var variant = new NPVariant
            {
                type = NPVariantType.Object,
                value = new NPVariantValue { objectValue = objPtr }
            };

            Marshal.StructureToPtr(variant, variantPtr, false);
        }

        public static void SetNPVariantString(IntPtr variantPtr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr + bytes.Length, 0);

            lock (s_allocatedVariantStrings)
            {
                s_allocatedVariantStrings.Add(ptr);
            }

            var variant = new NPVariant
            {
                type = NPVariantType.String,
                value = new NPVariant.NPVariantValue
                {
                    stringValue = new NPString
                    {
                        UTF8Characters = ptr,
                        UTF8Length = (uint)bytes.Length
                    }
                }
            };

            Marshal.StructureToPtr(variant, variantPtr, false);
        }

    }
}
