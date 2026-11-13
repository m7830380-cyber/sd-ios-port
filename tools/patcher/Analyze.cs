using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Analyze
{
    static readonly string[] Interesting =
    {
        "UnityEngine.Input", "UnityEngine.Cursor", "UnityEngine.Application", "UnityEngine.Screen",
        "UnityEngine.QualitySettings", "UnityEngine.TouchScreenKeyboard", "System.Diagnostics.Process",
        "Microsoft.Win32.", "System.Windows.Forms.", "System.Reflection.Emit.", "HarmonyLib.",
        "System.Net.Sockets.", "System.IO.Pipes.", "UnityEngine.Microphone", "UnityEngine.SystemInfo", "UnityEngine.Video.", "System.Environment",
    };

    public static int Run(string dir)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dir);
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("UnityEngine.") && name != "UnityEngine.UI.dll") continue;
            if (name.StartsWith("System.") || name.StartsWith("Mono.") || name == "mscorlib.dll" || name == "netstandard.dll") continue;
            AssemblyDefinition asm;
            try { asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver }); }
            catch (Exception e) { Console.WriteLine($"== {name}: unreadable ({e.Message})"); continue; }
            var mod = asm.MainModule;
            Console.WriteLine($"== {name}  refs: {string.Join(", ", mod.AssemblyReferences.Select(r => r.Name))}");
            var uses = new SortedDictionary<string, int>();
            int pinv = 0;
            foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (m.IsPInvokeImpl) pinv++;
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (!(ins.Operand is MemberReference mr) || mr.DeclaringType == null) continue;
                        string dt = mr.DeclaringType.FullName;
                        if (!Interesting.Any(p => dt == p || (p.EndsWith(".") && dt.StartsWith(p)))) continue;
                        string key = dt + "::" + mr.Name + (mr is MethodReference meth ? "(" + string.Join(",", meth.Parameters.Select(p => p.ParameterType.Name)) + ")" : "");
                        uses[key] = uses.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                }
            if (pinv > 0) Console.WriteLine($"   pinvokes: {pinv}");
            foreach (var kv in uses) Console.WriteLine($"   {kv.Value,4}  {kv.Key}");
        }
        return 0;
    }

    public static int DumpIL(string path, string what)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        var mod = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver }).MainModule;
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.FullName.Contains(what)) continue;
                Console.WriteLine($"== {m.FullName}  body={m.HasBody} rva={m.RVA} impl={m.ImplAttributes}");
                if (!m.HasBody) continue;
                Console.WriteLine($"   maxstack={m.Body.MaxStackSize} locals={m.Body.Variables.Count} initlocals={m.Body.InitLocals}");
                foreach (var ins in m.Body.Instructions) Console.WriteLine("   " + ins);
            }
        return 0;
    }

    public static int Types(string dir, string what)
    {
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p))
        {
            ModuleDefinition mod;
            try { mod = ModuleDefinition.ReadModule(path); } catch { continue; }
            foreach (var t in mod.GetTypes())
                if (t.FullName.Contains(what)) Console.WriteLine($"{Path.GetFileName(path)}: {t.FullName}");
            foreach (var e in mod.ExportedTypes)
                if (e.FullName.Contains(what)) Console.WriteLine($"{Path.GetFileName(path)}: forwarder {e.FullName} -> {e.Scope}");
        }
        return 0;
    }

    public static int Calls(string dir, string what)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dir);
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p))
        {
            AssemblyDefinition asm;
            try { asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver }); }
            catch { continue; }
            foreach (var t in asm.MainModule.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                        if (ins.Operand is MethodReference mr && mr.FullName.Contains(what))
                            Console.WriteLine($"{Path.GetFileName(path)}: {m.FullName} -> {mr.FullName}");
                }
        }
        return 0;
    }
}
