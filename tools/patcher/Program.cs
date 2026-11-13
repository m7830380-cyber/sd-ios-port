using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Program
{
    static bool IsEngineOrBcl(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file);
        if (n == "UnityEngine.UI") return false;
        return n == "UnityEngine" || n.StartsWith("UnityEngine.") || n.StartsWith("System") || n.StartsWith("Mono.") ||
               n == "mscorlib" || n == "netstandard" || n.StartsWith("Microsoft.");
    }

    static readonly HashSet<string> InputRedirect = new HashSet<string>
    {
        "Assembly-CSharp",
        "Assembly-CSharp-firstpass",
        "UnityEngine.UI",
        "Unity.TextMeshPro",
        "Rewired_Core",
        "Rewired_Windows",
        "Rewired_Windows_Functions",
        "ch.sycoforge.Decal",
    };

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "analyze") return Analyze.Run(args[1]);
        if (args.Length >= 3 && args[0] == "dumpil") return Analyze.DumpIL(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "types") return Analyze.Types(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "calls") return Analyze.Calls(args[1], args[2]);
        if (args.Length >= 4 && args[0] == "patch") return Patch(args[1], args[2], args[3]);
        Console.Error.WriteLine("usage: SdPatcher patch <managedDir> <SdShim.dll> <outDir> | analyze <managedDir>");
        return 1;
    }

    static int Patch(string inDir, string shimPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(inDir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(shimPath)));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, ReadSymbols = false };
        var shim = AssemblyDefinition.ReadAssembly(shimPath, rp);

        var shipped = Directory.GetFiles(inDir, "*.dll").Where(f => !IsEngineOrBcl(f)).OrderBy(f => f).ToList();
        int total = 0, failures = 0;
        foreach (var path in shipped)
        {
            string name = Path.GetFileName(path);
            var asm = AssemblyDefinition.ReadAssembly(path, rp);
            var ctx = new Ctx(asm.MainModule, shim.MainModule);

            int n = StubPInvokes(ctx);
            n += RedirectProcess(ctx);
            n += RedirectMicrophone(ctx);

            if (InputRedirect.Contains(asm.Name.Name))
            {
                n += RedirectInput(ctx);
            }

            if (asm.Name.Name == "Assembly-CSharp")
            {
                n += RedirectCursor(ctx);
                n += RedirectFrameRate(ctx);
                n += PatchSteamManager(ctx);
                n += PatchRewiredTools(ctx);
            }
            else if (asm.Name.Name == "Assembly-CSharp-firstpass")
            {
                n += PatchFilePath(ctx);
            }
            else if (asm.Name.Name == "Unity.Postprocessing.Runtime")
            {
                n += NoComputeShaders(ctx);
            }
            else if (asm.Name.Name == "Sirenix.Serialization")
            {
                n += PatchSirenixSerialization(ctx);
            }
            else if (asm.Name.Name == "Sirenix.Utilities")
            {
                n += PatchSirenixUtilities(ctx);
            }

            string outPath = Path.Combine(outDir, name);
            asm.Write(outPath);
            Console.WriteLine($"patched {name,-35} ({n} sites){(ctx.Errors.Count > 0 ? " WITH ERRORS:" : "")}");
            foreach (var err in ctx.Errors) { Console.WriteLine("  ! " + err); failures++; }
            total += n;
        }

        Console.WriteLine($"\nall assemblies patched: {total} total call sites modified across {shipped.Count} assemblies.");
        return failures == 0 ? 0 : 2;
    }

    sealed class Ctx
    {
        public readonly ModuleDefinition Mod;
        public readonly ModuleDefinition Shim;
        public readonly List<string> Errors = new List<string>();

        public Ctx(ModuleDefinition mod, ModuleDefinition shim)
        {
            Mod = mod;
            Shim = shim;
        }

        public void Fail(string msg) => Errors.Add(msg);
    }

    static IEnumerable<(MethodDefinition, Instruction)> Calls(ModuleDefinition mod)
    {
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference)
                        yield return (m, ins);
            }
    }

    static int StubPInvokes(Ctx c)
    {
        int count = 0;
        foreach (var t in c.Mod.GetTypes())
            foreach (var m in t.Methods.ToList())
            {
                if (!m.IsPInvokeImpl) continue;
                var _ = m.PInvokeInfo;
                m.PInvokeInfo = null;
                m.IsPInvokeImpl = false;
                m.Attributes &= ~MethodAttributes.PInvokeImpl;
                m.ImplAttributes &= ~MethodImplAttributes.PreserveSig;
                m.ImplAttributes |= MethodImplAttributes.IL;

                m.Body = new MethodBody(m);
                var il = m.Body.GetILProcessor();
                var ret = m.ReturnType;

                if (ret.MetadataType == MetadataType.Void)
                {
                    il.Emit(OpCodes.Ret);
                }
                else if (ret.MetadataType == MetadataType.Boolean || ret.MetadataType == MetadataType.Int32 ||
                         ret.MetadataType == MetadataType.UInt32 || ret.MetadataType == MetadataType.Int16 ||
                         ret.MetadataType == MetadataType.UInt16 || ret.MetadataType == MetadataType.Byte ||
                         ret.MetadataType == MetadataType.SByte || ret.MetadataType == MetadataType.Char)
                {
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ret);
                }
                else if (ret.MetadataType == MetadataType.Int64 || ret.MetadataType == MetadataType.UInt64)
                {
                    il.Emit(OpCodes.Ldc_I8, 0L);
                    il.Emit(OpCodes.Ret);
                }
                else if (ret.MetadataType == MetadataType.Single)
                {
                    il.Emit(OpCodes.Ldc_R4, 0.0f);
                    il.Emit(OpCodes.Ret);
                }
                else if (ret.MetadataType == MetadataType.Double)
                {
                    il.Emit(OpCodes.Ldc_R8, 0.0);
                    il.Emit(OpCodes.Ret);
                }
                else if (ret.MetadataType == MetadataType.IntPtr)
                {
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Ret);
                }
                else if (ret.MetadataType == MetadataType.UIntPtr)
                {
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Conv_U);
                    il.Emit(OpCodes.Ret);
                }
                else if (ret.IsValueType)
                {
                    var loc = new VariableDefinition(ret);
                    m.Body.Variables.Add(loc);
                    m.Body.InitLocals = true;
                    il.Emit(OpCodes.Ldloca_S, loc);
                    il.Emit(OpCodes.Initobj, ret);
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Ret);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ret);
                }

                count++;
            }
        return count;
    }

    static int RedirectProcess(Ctx c)
    {
        var proc = c.Shim.GetType("SdShim.Platform");
        var target = proc?.Methods.FirstOrDefault(m => m.Name == "ProcessStart" && m.Parameters.Count == 1);
        if (target == null) return 0;
        var imported = c.Mod.ImportReference(target);

        int count = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName == "System.Diagnostics.Process" && mr.Name == "Start" && mr.Parameters.Count == 1 &&
                mr.Parameters[0].ParameterType.MetadataType == MetadataType.String)
            {
                ins.OpCode = OpCodes.Call;
                ins.Operand = imported;
                count++;
            }
        }
        return count;
    }

    static int RedirectMicrophone(Ctx c)
    {
        var mic = c.Shim.GetType("SdShim.Mic");
        if (mic == null) return 0;
        int count = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName == "UnityEngine.Microphone")
            {
                var replacement = mic.Methods.FirstOrDefault(x => x.Name == mr.Name && x.Parameters.Count == mr.Parameters.Count);
                if (replacement != null)
                {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = c.Mod.ImportReference(replacement);
                    count++;
                }
            }
        }
        return count;
    }

    static int RedirectInput(Ctx c)
    {
        var vinput = c.Shim.GetType("SdShim.VInput");
        if (vinput == null) { c.Fail("SdShim.VInput type not found"); return 0; }
        int count = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName == "UnityEngine.Input")
            {
                var target = vinput.Methods.FirstOrDefault(x => x.Name == mr.Name && x.Parameters.Count == mr.Parameters.Count);
                if (target != null)
                {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = c.Mod.ImportReference(target);
                    count++;
                }
            }
        }
        return count;
    }

    static int RedirectCursor(Ctx c)
    {
        var cursor = c.Shim.GetType("SdShim.IosCursor");
        if (cursor == null) return 0;
        int count = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName == "UnityEngine.Cursor")
            {
                if (mr.Name == "set_lockState")
                {
                    var target = cursor.Methods.First(x => x.Name == "SetLockState");
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = c.Mod.ImportReference(target);
                    count++;
                }
                else if (mr.Name == "set_visible")
                {
                    var target = cursor.Methods.First(x => x.Name == "SetVisible");
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = c.Mod.ImportReference(target);
                    count++;
                }
                else if (mr.Name == "get_lockState")
                {
                    var target = cursor.Methods.First(x => x.Name == "GetLockState");
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = c.Mod.ImportReference(target);
                    count++;
                }
                else if (mr.Name == "get_visible")
                {
                    var target = cursor.Methods.First(x => x.Name == "GetVisible");
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = c.Mod.ImportReference(target);
                    count++;
                }
            }
        }
        return count;
    }

    static int RedirectFrameRate(Ctx c)
    {
        var plat = c.Shim.GetType("SdShim.Platform");
        if (plat == null) return 0;
        int count = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName == "UnityEngine.Application" && mr.Name == "set_targetFrameRate")
            {
                var target = plat.Methods.First(x => x.Name == "SetTargetFrameRate");
                ins.OpCode = OpCodes.Call;
                ins.Operand = c.Mod.ImportReference(target);
                count++;
            }
            else if (mr.DeclaringType.FullName == "UnityEngine.QualitySettings" && mr.Name == "set_vSyncCount")
            {
                var target = plat.Methods.First(x => x.Name == "SetVSyncCount");
                ins.OpCode = OpCodes.Call;
                ins.Operand = c.Mod.ImportReference(target);
                count++;
            }
            else if (mr.DeclaringType.FullName == "UnityEngine.Screen" && mr.Name == "SetResolution")
            {
                var target = plat.Methods.First(x => x.Name == "SetResolution");
                ins.OpCode = OpCodes.Call;
                ins.Operand = c.Mod.ImportReference(target);
                count++;
            }
        }
        return count;
    }

    static int PatchSteamManager(Ctx c)
    {
        var sm = c.Mod.GetType("SteamManager");
        var awake = sm?.Methods.FirstOrDefault(m => m.Name == "Awake" && m.HasBody);
        if (awake == null) { c.Fail("SteamManager.Awake not found"); return 0; }

        var s_instance = sm.Fields.FirstOrDefault(f => f.Name == "s_instance");
        var m_bInitialized = sm.Fields.FirstOrDefault(f => f.Name == "m_bInitialized");
        if (s_instance == null || m_bInitialized == null) { c.Fail("SteamManager fields missing"); return 0; }

        // Find existing MethodReferences in the method body or module
        MethodReference getGoMethod = null;
        MethodReference dontDestroyMethod = null;
        foreach (var ins in awake.Body.Instructions)
        {
            if (ins.Operand is MethodReference mr)
            {
                if (mr.Name == "get_gameObject") getGoMethod = mr;
                else if (mr.Name == "DontDestroyOnLoad") dontDestroyMethod = mr;
            }
        }

        if (getGoMethod == null || dontDestroyMethod == null)
        {
            foreach (var (m, ins) in Calls(c.Mod))
            {
                var mr = (MethodReference)ins.Operand;
                if (getGoMethod == null && mr.Name == "get_gameObject") getGoMethod = mr;
                if (dontDestroyMethod == null && mr.Name == "DontDestroyOnLoad") dontDestroyMethod = mr;
                if (getGoMethod != null && dontDestroyMethod != null) break;
            }
        }

        if (getGoMethod == null || dontDestroyMethod == null)
        {
            c.Fail("Could not locate get_gameObject or DontDestroyOnLoad references");
            return 0;
        }

        awake.Body = new MethodBody(awake);
        var il = awake.Body.GetILProcessor();
        // s_instance = this
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stsfld, s_instance);
        // this.m_bInitialized = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, m_bInitialized);
        // DontDestroyOnLoad(this.gameObject)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, getGoMethod);
        il.Emit(OpCodes.Call, dontDestroyMethod);
        il.Emit(OpCodes.Ret);

        return 1;
    }

    static int PatchRewiredTools(Ctx c)
    {
        var extTools = c.Mod.GetType("Rewired.Utils.ExternalTools");
        if (extTools == null) { c.Fail("Rewired.Utils.ExternalTools not found"); return 0; }

        int count = 0;
        var getInit = extTools.Methods.FirstOrDefault(m => m.Name == "GetPlatformInitializer");
        if (getInit != null)
        {
            getInit.Body = new MethodBody(getInit);
            var il = getInit.Body.GetILProcessor();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            count++;
        }

        var fwdRaw = extTools.Methods.FirstOrDefault(m => m.Name == "WindowsStandalone_ForwardRawInput");
        if (fwdRaw != null)
        {
            fwdRaw.Body = new MethodBody(fwdRaw);
            var il = fwdRaw.Body.GetILProcessor();
            il.Emit(OpCodes.Ret);
            count++;
        }

        return count;
    }

    static int PatchFilePath(Ctx c)
    {
        var fp = c.Mod.GetType("FilePath");
        var cctor = fp?.Methods.FirstOrDefault(m => m.Name == ".cctor" && m.HasBody);
        if (cctor == null) { c.Fail("FilePath..cctor not found"); return 0; }

        int replaced = 0;
        foreach (var ins in cctor.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s)
            {
                if (s == "Data/Bundles/")
                {
                    ins.Operand = "Bundles/";
                    replaced++;
                }
                else if (s == "Data/Default Maps/")
                {
                    ins.Operand = "Default Maps/";
                    replaced++;
                }
            }
        }

        if (replaced < 2) c.Fail($"FilePath..cctor: expected 2 string replacements, found {replaced}");
        return replaced;
    }

    static int NoComputeShaders(Ctx c)
    {
        int count = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName == "UnityEngine.SystemInfo" && mr.Name == "get_supportsComputeShaders")
            {
                ins.OpCode = OpCodes.Ldc_I4_0;
                ins.Operand = null;
                count++;
            }
        }
        return count;
    }

    static MethodReference FindUnityObjectOpEquality(Ctx c)
    {
        var opEq = c.Mod.GetMemberReferences()
            .OfType<MethodReference>()
            .FirstOrDefault(m => m.DeclaringType.FullName == "UnityEngine.Object" && m.Name == "op_Equality");

        if (opEq != null) return opEq;

        foreach (var (m, ins) in Calls(c.Mod))
        {
            if (ins.Operand is MethodReference mr && mr.DeclaringType.FullName == "UnityEngine.Object" && mr.Name == "op_Equality")
                return mr;
        }
        return null;
    }

    static int PatchUnityExtensions(Ctx c, string typeName)
    {
        var unityExt = c.Mod.GetType(typeName);
        if (unityExt == null) return 0;

        int count = 0;
        var cctor = unityExt.Methods.FirstOrDefault(m => m.Name == ".cctor");
        if (cctor != null)
        {
            cctor.Body = new MethodBody(cctor);
            cctor.Body.GetILProcessor().Emit(OpCodes.Ret);
            count++;
        }

        var safeNull = unityExt.Methods.FirstOrDefault(m => m.Name == "SafeIsUnityNull");
        if (safeNull != null)
        {
            var opEq = FindUnityObjectOpEquality(c);
            if (opEq != null)
            {
                safeNull.Body = new MethodBody(safeNull);
                var il = safeNull.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Call, opEq);
                il.Emit(OpCodes.Ret);
                count++;
            }
            else
            {
                c.Fail($"Could not find UnityEngine.Object::op_Equality reference for {typeName}.SafeIsUnityNull");
            }
        }

        return count;
    }

    static int PatchSirenixSerialization(Ctx c)
    {
        int count = 0;

        // 1. EmitUtilities.get_CanEmit -> false
        var emitUtils = c.Mod.GetType("Sirenix.Serialization.Utilities.EmitUtilities");
        var getCanEmit = emitUtils?.Methods.FirstOrDefault(m => m.Name == "get_CanEmit");
        if (getCanEmit != null)
        {
            getCanEmit.Body = new MethodBody(getCanEmit);
            var il = getCanEmit.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            count++;
        }
        else
        {
            c.Fail("Sirenix.Serialization.Utilities.EmitUtilities.get_CanEmit not found");
        }

        // 2. FormatterEmitter.GetEmittedFormatter -> return null
        var formatterEmitter = c.Mod.GetType("Sirenix.Serialization.FormatterEmitter");
        var getEmitted = formatterEmitter?.Methods.FirstOrDefault(m => m.Name == "GetEmittedFormatter");
        if (getEmitted != null)
        {
            getEmitted.Body = new MethodBody(getEmitted);
            var il = getEmitted.Body.GetILProcessor();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            count++;
        }

        // 3. UnitySerializationUtility.GetCachedUnityMemberSetter / Getter -> branch to fallback reflection delegate
        var unitySerUtil = c.Mod.GetType("Sirenix.Serialization.UnitySerializationUtility");
        if (unitySerUtil != null)
        {
            count += BypassEmitToReflectionFallback(c, unitySerUtil.Methods.FirstOrDefault(m => m.Name == "GetCachedUnityMemberSetter"));
            count += BypassEmitToReflectionFallback(c, unitySerUtil.Methods.FirstOrDefault(m => m.Name == "GetCachedUnityMemberGetter"));
        }
        else
        {
            c.Fail("Sirenix.Serialization.UnitySerializationUtility not found");
        }

        // 4. BaseFormatter<T>.CreateCallback -> return null
        foreach (var t in c.Mod.GetTypes())
        {
            if (t.Name.StartsWith("BaseFormatter`1"))
            {
                var cbMethod = t.Methods.FirstOrDefault(m => m.Name == "CreateCallback");
                if (cbMethod != null)
                {
                    cbMethod.Body = new MethodBody(cbMethod);
                    var il = cbMethod.Body.GetILProcessor();
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ret);
                    count++;
                }
            }
        }

        // 5. UnityExtensions (.cctor and SafeIsUnityNull)
        count += PatchUnityExtensions(c, "Sirenix.Serialization.Utilities.UnityExtensions");

        return count;
    }

    static int PatchSirenixUtilities(Ctx c)
    {
        int count = 0;

        // 1. EmitUtilities.get_CanEmit -> false
        var emitUtils = c.Mod.GetType("Sirenix.Utilities.EmitUtilities");
        var getCanEmit = emitUtils?.Methods.FirstOrDefault(m => m.Name == "get_CanEmit");
        if (getCanEmit != null)
        {
            getCanEmit.Body = new MethodBody(getCanEmit);
            var il = getCanEmit.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            count++;
        }
        else
        {
            c.Fail("Sirenix.Utilities.EmitUtilities.get_CanEmit not found");
        }

        // 2. UnityExtensions (.cctor and SafeIsUnityNull)
        count += PatchUnityExtensions(c, "Sirenix.Utilities.UnityExtensions");

        return count;
    }

    static int BypassEmitToReflectionFallback(Ctx c, MethodDefinition method)
    {
        if (method == null || !method.HasBody)
        {
            c.Fail("Method missing or has no body in BypassEmitToReflectionFallback");
            return 0;
        }

        Instruction prevToLdftn = null;
        for (int i = 1; i < method.Body.Instructions.Count; i++)
        {
            if (method.Body.Instructions[i].OpCode == OpCodes.Ldftn)
            {
                prevToLdftn = method.Body.Instructions[i - 1];
                break;
            }
        }

        Instruction branchAfterTryGet = null;
        for (int i = 0; i < method.Body.Instructions.Count - 1; i++)
        {
            var ins = method.Body.Instructions[i];
            if (ins.Operand is MethodReference mr && mr.Name == "TryGetValue")
            {
                branchAfterTryGet = method.Body.Instructions[i + 1];
                break;
            }
        }

        if (branchAfterTryGet == null || prevToLdftn == null)
        {
            c.Fail($"Could not locate patch points in {method.Name}");
            return 0;
        }

        int branchIdx = method.Body.Instructions.IndexOf(branchAfterTryGet);
        var targetIns = method.Body.Instructions[branchIdx + 1];
        targetIns.OpCode = OpCodes.Br;
        targetIns.Operand = prevToLdftn;
        return 1;
    }
}
