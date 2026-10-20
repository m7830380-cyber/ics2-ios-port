// Patches the retail Internet Cafe Simulator 2 managed assemblies so they can be AOT-compiled with IL2CPP
// and run on iOS.
//
//   Ics2Patcher patch   <retailManagedDir> <ICS2Shim.dll> <outDir>
//   Ics2Patcher analyze <managedDir>
//
// All shipped assemblies:
//  * every P/Invoke (steam_api64, the Vuplex Windows/Chromium plugin) becomes a managed stub that throws
//    DllNotFoundException, so IL2CPP never links against native libraries that don't exist on iOS.
//  * Process.Start & co. go to ICS2Shim.Platform (logs and does nothing; http(s) URLs open in Safari).
//  * every UnityEngine.Input member goes to ICS2Shim.VInput (virtual mouse/keys driven by touch).
//  * SystemInfo.supportsComputeShaders reads false (the DX11 compute kernels have no Metal translation).
// Assembly-CSharp:
//  * Steam: SteamManager never initialises the API; achievement / language calls go to ICS2Shim.SteamOffline
//    (achievements are kept in Documents, the language comes from the iOS settings).
//  * no network: Weather.Start (ipify -> geoplugin -> openweathermap with the player's IP) does nothing (the
//    iOS mod fills the widget with offline weather), I2 Localization never downloads its Google sheet.
//  * vSyncCount / targetFrameRate go through ICS2Shim.Platform (iOS needs an explicit frame rate).
//  * Application.dataPath (screenshots) -> persistentDataPath (the app bundle is read-only).
//  * Vuplex's plugin factory is made public so the iOS mod can register its own web view backend.
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Program
{
    // Assemblies shipped in the iOS player (patched copies go to outDir). Everything else in Managed/ is
    // UnityEngine.* or the .NET profile, which the shell provides itself.
    public static readonly string[] Shipped =
    {
        "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "BakeryRuntimeAssembly.dll",
        "Boxophobic.AtmosphericHeightFog.Runtime.dll", "Boxophobic.Utils.Scripts.dll", "CFXRDemo.dll", "CFXRRuntime.dll",
        "DOTween.dll", "FileBasedPrefs.dll", "PathCreator.dll", "SharpConfig.dll", "Unity.MemoryProfiler.dll",
        "Unity.Postprocessing.Runtime.dll", "Unity.TextMeshPro.dll", "Unity.Timeline.dll", "UnityEngine.UI.dll",
        "com.rlabrecque.steamworks.net.dll",
    };

    static readonly HashSet<string> NoInputRedirect = new HashSet<string> { "com.rlabrecque.steamworks.net" };

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "analyze") return Analyze.Run(args[1]);
        if (args.Length >= 2 && args[0] == "stackcheck") return Analyze.StackCheck(args.Skip(1).ToArray());
        if (args.Length >= 3 && args[0] == "dumpil") return Analyze.DumpIL(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "types") return Analyze.Types(args[1], args[2]);
        if (args.Length >= 4 && args[0] == "stubmethods") return Analyze.StubMethods(args[1], args[2], args[3]);
        if (args.Length >= 3 && args[0] == "memberrefs") return Analyze.MemberRefs(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "calls") return Analyze.Calls(args[1], args[2]);
        if (args.Length >= 4 && args[0] == "patch") return Patch(args[1], args[2], args[3]);
        Console.Error.WriteLine("usage: Ics2Patcher patch <managedDir> <ICS2Shim.dll> <outDir> | analyze <managedDir>");
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

        int total = 0, failures = 0;
        foreach (var name in Shipped)
        {
            var asm = AssemblyDefinition.ReadAssembly(Path.Combine(inDir, name), rp);
            var ctx = new Ctx(asm.MainModule, shim.MainModule);
            int n = StubPInvokes(ctx);
            n += RedirectProcess(ctx);
            n += RemoveTypedReferences(ctx);
            n += NoComputeShaders(ctx);
            if (!NoInputRedirect.Contains(asm.Name.Name))
                n += RedirectInput(ctx);
            if (asm.Name.Name == "Assembly-CSharp")
                n += PatchGame(ctx);
            total += n;
            failures += ctx.Failures;
            asm.Write(Path.Combine(outDir, name));
            Console.WriteLine($"{name}: {n} patches{(ctx.Failures > 0 ? $", {ctx.Failures} FAILED" : "")}");
        }
        File.Copy(shimPath, Path.Combine(outDir, "ICS2Shim.dll"), true);
        Console.WriteLine($"total {total} patches, {failures} failures");
        return failures == 0 ? 0 : 2;
    }

    sealed class Ctx
    {
        public readonly ModuleDefinition Mod;
        public readonly ModuleDefinition Shim;
        public int Failures;
        public Ctx(ModuleDefinition mod, ModuleDefinition shim) { Mod = mod; Shim = shim; }

        public TypeDefinition ShimType(string name) => Shim.GetType("ICS2Shim." + name) ?? throw new Exception("shim type missing: " + name);

        public MethodReference ShimMethod(string type, string name, params string[] paramTypes)
        {
            var m = ShimType(type).Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == paramTypes.Length &&
                x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(paramTypes));
            if (m == null) throw new Exception($"shim method missing: {type}.{name}({string.Join(",", paramTypes)})");
            return Mod.ImportReference(m);
        }

        public void Fail(string what)
        {
            Console.WriteLine("  !! " + what);
            Failures++;
        }
    }

    static IEnumerable<(MethodDefinition m, Instruction ins)> Calls(ModuleDefinition mod)
    {
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions.ToList())
                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference)
                        yield return (m, ins);
            }
    }

    static string Sig(MethodReference mr) => string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName));

    // ------------------------------------------------------------------------------------------
    static int StubPInvokes(Ctx c)
    {
        int n = 0;
        var mod = c.Mod;
        var dnfType = new TypeReference("System", "DllNotFoundException", mod, mod.TypeSystem.CoreLibrary);
        var dnfCtor = new MethodReference(".ctor", mod.TypeSystem.Void, dnfType) { HasThis = true };
        dnfCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.IsPInvokeImpl || m.PInvokeInfo == null) continue;
                string lib = m.PInvokeInfo.Module.Name;
                m.PInvokeInfo = null;     // Cecil's setter turns IsPInvokeImpl back on,
                m.IsPInvokeImpl = false;  // so clear the flag afterwards
                m.IsPreserveSig = false;
                m.ImplAttributes &= ~MethodImplAttributes.PreserveSig;
                m.ImplAttributes |= MethodImplAttributes.IL | MethodImplAttributes.Managed;
                m.Body = new MethodBody(m);
                var il = m.Body.GetILProcessor();
                il.Emit(OpCodes.Ldstr, lib + " is not available on iOS");
                il.Emit(OpCodes.Newobj, dnfCtor);
                il.Emit(OpCodes.Throw);
                n++;
            }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectProcess(Ctx c)
    {
        int n = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "System.Diagnostics.Process") continue;
            MethodReference to = null;
            if (mr.Name == "Start" && !mr.HasThis)
            {
                string sig = Sig(mr);
                if (sig == "System.Diagnostics.ProcessStartInfo") to = c.ShimMethod("Platform", "ProcessStart", "System.Diagnostics.ProcessStartInfo");
                else if (sig == "System.String") to = c.ShimMethod("Platform", "ProcessStart", "System.String");
                else if (sig == "System.String,System.String") to = c.ShimMethod("Platform", "ProcessStart", "System.String", "System.String");
                else { c.Fail($"{m.FullName}: unhandled Process.Start({sig})"); continue; }
            }
            else if (mr.Name == "Start" && mr.HasThis && mr.Parameters.Count == 0)
                to = c.ShimMethod("Platform", "ProcessStartInstance", "System.Diagnostics.Process");
            else if (mr.Name == "GetProcessesByName" && Sig(mr) == "System.String")
                to = c.ShimMethod("Platform", "GetProcessesByName", "System.String");
            if (to == null) continue;
            ins.OpCode = OpCodes.Call;
            ins.Operand = to;
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectInput(Ctx c)
    {
        int n = 0;
        var vinput = c.ShimType("VInput");
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "UnityEngine.Input") continue;
            var target = vinput.Methods.FirstOrDefault(x => x.Name == mr.Name && x.IsStatic &&
                x.Parameters.Count == mr.Parameters.Count && Sig(x) == Sig(mr) && x.ReturnType.FullName == mr.ReturnType.FullName);
            if (target == null)
            {
                c.Fail($"{m.FullName}: no VInput equivalent for Input.{mr.Name}({Sig(mr)})");
                continue;
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = c.Mod.ImportReference(target);
            n++;
        }
        return n;
    }

    // Compute kernels (PPv2 auto exposure / 3D LUT / MSVO, PPv1 eye adaptation, Enviro / LightingBox DX11
    // paths) are DX11 bytecode without a Metal translation: report compute as unsupported so every effect
    // takes its pixel-shader path instead of failing every frame.
    static int NoComputeShaders(Ctx c)
    {
        int n = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName == "UnityEngine.SystemInfo" && mr.Name == "get_supportsComputeShaders")
            {
                ins.OpCode = OpCodes.Ldc_I4_0;
                ins.Operand = null;
                n++;
            }
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static readonly Regex PathLiteral = new Regex(@"^[\w .\-]+(\\[\w .\-]*)+$");

    // IL2CPP's stack analysis does not model mkrefany (C# __makeref); report any use so it can be rewritten.
    static int RemoveTypedReferences(Ctx c)
    {
        foreach (var t in c.Mod.GetTypes())
            foreach (var m in t.Methods)
                if (m.HasBody && m.Body.Instructions.Any(i => i.OpCode.Code == Code.Mkrefany || i.OpCode.Code == Code.Refanyval ||
                                                             i.OpCode.Code == Code.Refanytype || i.OpCode.Code == Code.Arglist))
                    c.Fail("typed reference (__makeref) in " + m.FullName + " - IL2CPP cannot convert it");
        return 0;
    }

    static int PatchGame(Ctx c)
    {
        var mod = c.Mod;
        int n = 0;

        // vSync/targetFrameRate -> shim, Application.dataPath -> persistentDataPath
        var setVSync = c.ShimMethod("Platform", "SetVSyncCount", "System.Int32");
        var setFps = c.ShimMethod("Platform", "SetTargetFrameRate", "System.Int32");
        var dataPath = c.ShimMethod("Platform", "get_WritableDataPath");
        var persistentPath = c.ShimMethod("Platform", "get_PersistentDataPath");
        var steam = c.ShimType("SteamOffline");
        foreach (var (m, ins) in Calls(mod))
        {
            var mr = (MethodReference)ins.Operand;
            string dt = mr.DeclaringType.FullName;
            if (dt == "UnityEngine.QualitySettings" && mr.Name == "set_vSyncCount")
            {
                ins.OpCode = OpCodes.Call; ins.Operand = setVSync; n++;
            }
            else if (dt == "UnityEngine.Application" && mr.Name == "set_targetFrameRate")
            {
                ins.OpCode = OpCodes.Call; ins.Operand = setFps; n++;
            }
            else if (dt == "UnityEngine.Application" && mr.Name == "get_dataPath")
            {
                ins.OpCode = OpCodes.Call; ins.Operand = dataPath; n++;
            }
            else if (dt == "UnityEngine.Application" && mr.Name == "get_persistentDataPath")
            {
                ins.OpCode = OpCodes.Call; ins.Operand = persistentPath; n++;
            }
            else if (dt == "UnityEngine.Screen" && (mr.Name == "SetResolution" || mr.Name == "set_fullScreen" || mr.Name == "set_fullScreenMode"))
            {
                string name = mr.Name == "set_fullScreen" ? "SetFullScreen" : mr.Name == "set_fullScreenMode" ? "SetFullScreenMode" : mr.Name;
                var target = c.ShimType("Platform").Methods.FirstOrDefault(x => x.Name == name && x.IsStatic && Sig(x) == Sig(mr));
                if (target == null) { c.Fail($"{m.FullName}: no Platform equivalent for Screen.{mr.Name}({Sig(mr)})"); continue; }
                ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(target); n++;
            }
            else if ((dt == "Steamworks.SteamUserStats" || dt == "Steamworks.SteamApps") && m.DeclaringType.Name != "SteamManager")
            {
                var target = steam.Methods.FirstOrDefault(x => x.Name == mr.Name && x.IsStatic && Sig(x) == Sig(mr) &&
                                                               x.ReturnType.FullName == mr.ReturnType.FullName);
                if (target == null) { c.Fail($"{m.FullName}: no SteamOffline equivalent for {dt}.{mr.Name}({Sig(mr)})"); continue; }
                ins.OpCode = OpCodes.Call; ins.Operand = mod.ImportReference(target); n++;
            }
            else if (dt.StartsWith("Steamworks.") && m.DeclaringType.Name != "SteamManager" && !m.DeclaringType.FullName.StartsWith("Steamworks"))
            {
                c.Fail($"{m.FullName}: unhandled Steam call {dt}.{mr.Name}");
            }
        }

        // FBPP save file name -> Platform.SaveFileName (co-op guests play in a session file)
        {
            var saveName = c.ShimMethod("Platform", "get_SaveFileName");
            int hits = 0;
            foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                        if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "save-data.save")
                        {
                            ins.OpCode = OpCodes.Call; ins.Operand = saveName; hits++;
                        }
                }
            if (hits != 2) c.Fail($"expected 2 uses of \"save-data.save\" (SaveManager, MainMenuManager), found {hits}");
            n += hits;
        }

        // Windows path separators in path-like string literals
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Ldstr) continue;
                    var s = (string)ins.Operand;
                    if (PathLiteral.IsMatch(s))
                    {
                        ins.Operand = s.Replace('\\', '/');
                        n++;
                    }
                }
            }

        // --- Steam: never initialise ----------------------------------------------------------
        {
            var sm = mod.GetType("SteamManager") ?? throw new Exception("SteamManager missing");
            var awake = sm.Methods.First(x => x.Name == "Awake");
            var inst = sm.Fields.First(x => x.Name == "s_instance");
            var core = mod.AssemblyReferences.First(r => r.Name == "UnityEngine.CoreModule");
            var objType = new TypeReference("UnityEngine", "Object", mod, core);
            var ddol = new MethodReference("DontDestroyOnLoad", mod.TypeSystem.Void, objType);
            ddol.Parameters.Add(new ParameterDefinition(objType));
            var destroy = new MethodReference("Destroy", mod.TypeSystem.Void, objType);
            destroy.Parameters.Add(new ParameterDefinition(objType));
            var opNe = new MethodReference("op_Inequality", mod.TypeSystem.Boolean, objType);
            opNe.Parameters.Add(new ParameterDefinition(objType));
            opNe.Parameters.Add(new ParameterDefinition(objType));
            var debugType = new TypeReference("UnityEngine", "Debug", mod, core);
            var log = new MethodReference("Log", mod.TypeSystem.Void, debugType);
            log.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Object));
            var compType = new TypeReference("UnityEngine", "Component", mod, core);
            var getGo = new MethodReference("get_gameObject", new TypeReference("UnityEngine", "GameObject", mod, core), compType) { HasThis = true };

            // if (s_instance != null) { Destroy(gameObject); return; }
            // s_instance = this; DontDestroyOnLoad(gameObject); Debug.Log("..."); (m_bInitialized stays false)
            awake.Body = new MethodBody(awake);
            var il = awake.Body.GetILProcessor();
            var go = Instruction.Create(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldsfld, inst);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, opNe);
            il.Emit(OpCodes.Brfalse_S, go);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, getGo);
            il.Emit(OpCodes.Call, destroy);
            il.Emit(OpCodes.Ret);
            il.Append(go);
            il.Emit(OpCodes.Stsfld, inst);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, getGo);
            il.Emit(OpCodes.Call, ddol);
            il.Emit(OpCodes.Ldstr, "[ICS2-iOS] Steamworks disabled on iOS (achievements are kept offline)");
            il.Emit(OpCodes.Call, log);
            il.Emit(OpCodes.Ret);
            n++;
        }

        // --- no network -----------------------------------------------------------------------
        n += MakeNoOp(c, mod.GetType("Weather"), "Start");

        // --- settings: the resolution list has one entry on iOS, a saved desktop index threw in
        // SettingMenu.Start and skipped loading the remaining settings (volume, AA, motion blur ...)
        n += MakeNoOp(c, mod.GetType("SettingMenu"), "SetResolution");

        // --- co-op: the host runs the shared world; on a guest these return at once ----------------
        foreach (var (type, method) in new[]
        {
            ("CivilManager", "SendCustomer"),           // customers arrive (hourly)
            ("CrackManager", "SpawnCrackOrder"),        // crack orders for the PC side job
            ("SecondCafeManager", "CabinAddMoneyHours"),// second cafe income (hourly)
            ("GameManager", "DailyChanges"),            // day rollover: bills, NPC resets, day++ (host sleeps)
        })
            n += GuardGuest(c, mod.GetType(type), method);

        // --- Enviro sky: Blit(CameraTarget, CameraTarget, sky) reads the target it writes. Metal keeps the old
        // frames ("hall of mirrors"); the sky shader never samples its source, so Platform.SkyBlit blits black.
        {
            var refresh = mod.GetType("EnviroSkyRendering")?.Methods.FirstOrDefault(x => x.Name == "RefreshCameraCommand");
            var skyBlit = c.ShimMethod("Platform", "SkyBlit", "UnityEngine.Rendering.CommandBuffer",
                "UnityEngine.Rendering.RenderTargetIdentifier", "UnityEngine.Rendering.RenderTargetIdentifier", "UnityEngine.Material");
            int hits = 0;
            if (refresh != null)
                foreach (var ins in refresh.Body.Instructions)
                    if ((ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call) && ins.Operand is MethodReference mr &&
                        mr.DeclaringType.FullName == "UnityEngine.Rendering.CommandBuffer" && mr.Name == "Blit" &&
                        Sig(mr) == "UnityEngine.Rendering.RenderTargetIdentifier,UnityEngine.Rendering.RenderTargetIdentifier,UnityEngine.Material")
                    {
                        ins.OpCode = OpCodes.Call; ins.Operand = skyBlit; hits++;
                    }
            if (hits != 1) c.Fail($"EnviroSkyRendering.RefreshCameraCommand: expected 1 sky blit, found {hits}");
            n += hits;
        }
        {
            var lsd = mod.GetType("I2.Loc.LanguageSourceData");
            var imp = lsd?.Methods.FirstOrDefault(x => x.Name == "Import_Google" && x.ReturnType.FullName == "System.Void");
            if (imp == null) c.Fail("I2.Loc.LanguageSourceData.Import_Google missing");
            else { imp.Body = new MethodBody(imp); imp.Body.GetILProcessor().Emit(OpCodes.Ret); n++; }
        }

        // --- Vuplex: the iOS mod registers its own IWebPlugin through WebPluginFactory -----------
        foreach (var tn in new[] { "Vuplex.WebView.WebPluginFactory", "Vuplex.WebView.IWebPlugin", "Vuplex.WebView.MockWebPlugin", "Vuplex.WebView.MockWebView" })
        {
            var t = mod.GetType(tn);
            if (t == null) { c.Fail(tn + " missing"); continue; }
            t.Attributes = (t.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.Public;
            n++;
        }
        return n;
    }

    /// Prepends `if (Platform.IsCoopGuest) return;` to a void method.
    static int GuardGuest(Ctx c, TypeDefinition t, string name)
    {
        var m = t?.Methods.FirstOrDefault(x => x.Name == name && x.ReturnType.FullName == "System.Void" && x.HasBody);
        if (m == null) { c.Fail($"{t?.Name}.{name} missing (guest guard)"); return 0; }
        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Call, c.ShimMethod("Platform", "get_IsCoopGuest")));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        return 1;
    }

    static int MakeNoOp(Ctx c, TypeDefinition t, string name)
    {
        var m = t?.Methods.FirstOrDefault(x => x.Name == name && x.ReturnType.FullName == "System.Void");
        if (m == null) { c.Fail($"{t?.Name}.{name} missing"); return 0; }
        m.Body = new MethodBody(m);
        m.Body.GetILProcessor().Emit(OpCodes.Ret);
        return 1;
    }
}
