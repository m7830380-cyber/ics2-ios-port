// Co-op hooks (LAN co-op, see mod/Coop):
//  * FBPP writes are reported to ICS2Shim.Platform.OnPrefSet, so the world's progress (rooms bought, quests,
//    skills, bills ...) stays the same on every device.
//  * the game methods listed in Hooks start with
//      if (Platform.CoopActive && Platform.Call(this, "Type.Method", new object[] { args })) return;
//    The mod decides per action: run here and on the other devices (doors, lights, elevators), or only on
//    the host (things that roll dice or pay money). See mod/Coop/CallSync.cs for what each one does.
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

static class CoopHooks
{
    /// "Type.Method" or "Type.Method/<parameter count>" for overloads. Kept in step with CallSync.Rules.
    public static readonly string[] Hooks =
    {
        // doors, lights, power, the cafe sign
        "LightSwitch.OpenLight", "LightSwitch.CloseLight", "Door.OpenDoor", "Door.CloseDoor",
        "ElectricalSwitch.SwitchOpened", "ElectricalSwitch.SwitchClosed", "OpenableItem.Interact",
        "CivilManager.OpenShop", "CivilManager.CloseShop", "OpenCloseSwitch.Clicked", "OpenCloseSwitch.ForceCloseSwitch",
        // elevators
        "OldElevator.ElevatorButton.Press", "OldElevator.DoorTrigger.Toggle",
        "OldElevator.ElevatorBrain.GoToPosition", "OldElevator.ElevatorBrain.EmergencyStopBegin",
        // kitchen and appliances
        "Toaster.AddBread", "Toaster.StartToast", "CoffeeMachine.JugDropped", "CoffeeMachine.StartThePourCoffee",
        "CoffeeCup.Fill", "Oven.StartCookingButton", "Oven.StopCookingButton", "HomeFridge.AddFood", "HomeFridge.MinesFood",
        "VendingMachine.AddNumberToMyOrder", "VendingMachine.ResetMachine", "VendingMachine.CompleteOrder",
        "Fan.StartFan", "Fan.StopFan", "MinerRigCase.AddNewGraphicCard", "CdManager.OpenedCDROm", "AmongDie.ClickedAmogus",
        // dirt, toilets, fire, temperature
        "objectInteraction.Siphon", "FootstepsManager.DestroyMe", "FootstepsManager.SpawnFootPrints",
        "FootstepsManager.RandomSpawnTrash", "FootstepsManager.SpawnPoo", "FootstepsManager.RandomSpawnPoo",
        "TableManager.SpawnRandomFire", "FireableItem.SpawnFire", "Fire.FireEnd", "SmokeDetector.SmokeDetecterON",
        "CafeTemperatureManager.ChangeTemp", "CafeTemperatureManager.ChangeTempCold",
        // buying the cafe's rooms, decorations, second cafe, staff
        "UpgradeItem.Buy", "UpgradeItem.TextureBuy", "SecondCafeManager.BuySecondCafe", "SecondCafeManager.BuyMoreCabin",
        "GameManager.ChangeLedColor", "Employee.RecruitButton", "Employee.FireButton",
        // bills and mail
        "BillManager.SpawnNewBill", "BillManager.PayedThisBill", "BillManager.PayAllBtn", "MailManager.SpawnMail",
        // the town: events only the host rolls, things only the host's people do
        "Robber.StealItem", "Bomberman.GoBomb", "Policeman.ShopControl", "Chef.AddNewOrder",
        "RaceManager.StartRace", "RaceManager.StopRace", "RaceManager.OpenWinnerPanel", "RivalInternetCafe.SetFakeCustomers",
        "Mascot.MascotGoHome", "EmployeeManager.OpenMascot", "Bouncer.SetTarget", "CheckAIMoneyPayed.OnTriggerEnter",
        "CİvilOptimizer.OpenRandomFiveCivils", "SlotMachineClub.Roll", "StripClubBouncer.EntranceFeePayed",
        "BunnyGirl.Payed", "PirateGamer.DeliveryCompleted", "Civil.DeliveredOrder",
        // customers' PCs and food orders
        "TableController.Approved", "TableController.Canceled", "TableController.ExtraTimeApproved",
        "TableController.MonitorOpened", "TableController.CivilGetUp", "FoodManager.SendFoodRequest",
        "OrderRequest.OrderDelivered", "OrderRequest.ApproveOrder", "OrderRequest.CloseButton",
        "Civil.CustomerForceOrderDeliveredComment",
        // second cafe cabins
        "Cabin.SetCivilAnimAndPos", "Cabin.UpgradeCabin",
        // knocked down by the wave gun / the bomb: the host's people react on the host
        "Civil.PunchSimulate", "Robber.PunchSimulate", "Bomberman.PunchRagdoll",
        // a guest's punches land on the host's people
        "Robber.OnTriggerEnter", "Bomberman.OnTriggerEnter", "Civil.OnTriggerEnter",
        // a fighting customer's blow (animation event): the host decides whose body it hits
        "Civil.Attacked",
    };

    /// Civil.Update reads GameManager.player (the one to chase and hit when fighting) through
    /// Platform.FightTarget(player, this), so in co-op the customer can go for the nearest player.
    public static int PatchFightTarget(Program.Ctx c)
    {
        var civil = c.Mod.GetType("Civil");
        var update = civil?.Methods.FirstOrDefault(x => x.Name == "Update" && x.HasBody && x.Parameters.Count == 0);
        if (update == null) { c.Fail("Civil.Update missing (fight target)"); return 0; }
        var target = c.ShimMethod("Platform", "FightTarget", "UnityEngine.Transform", "System.Object");
        var health = c.ShimMethod("Platform", "FightHealth", "System.Single", "System.Object");
        update.Body.SimplifyMacros();   // short branches may no longer reach
        var il = update.Body.GetILProcessor();
        int n = 0, h = 0;
        foreach (var ins in update.Body.Instructions.ToList())
        {
            if (ins.OpCode != OpCodes.Ldfld || !(ins.Operand is FieldReference f)) continue;
            MethodReference shim;
            if (f.Name == "player" && f.DeclaringType.FullName == "GameManager") { shim = target; n++; }
            else if (f.Name == "health" && f.DeclaringType.FullName == "PlayerRay") { shim = health; h++; }
            else continue;
            il.InsertAfter(ins, il.Create(OpCodes.Call, shim));
            il.InsertAfter(ins, il.Create(OpCodes.Ldarg_0));
        }
        update.Body.OptimizeMacros();
        if (n != 2) c.Fail($"Civil.Update: {n} reads of GameManager.player (expected 2)");
        if (h != 1) c.Fail($"Civil.Update: {h} reads of PlayerRay.health (expected 1)");
        return n > 0 ? 1 : 0;
    }

    public static int PatchPrefs(Program.Ctx c)
    {
        int n = 0;
        var fbpp = c.Mod.GetType("FBPP");
        if (fbpp == null) { c.Fail("FBPP missing"); return 0; }
        var onSet = c.ShimMethod("Platform", "OnPrefSet", "System.String", "System.Object");
        foreach (var name in new[] { "SetString", "SetInt", "SetFloat", "SetBool", "DeleteKey", "DeleteString", "DeleteInt", "DeleteFloat", "DeleteBool" })
        {
            var m = fbpp.Methods.FirstOrDefault(x => x.Name == name && x.IsStatic && x.HasBody);
            if (m == null) { c.Fail("FBPP." + name + " missing"); continue; }
            var il = m.Body.GetILProcessor();
            var first = m.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            if (m.Parameters.Count == 2)
            {
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
                var pt = m.Parameters[1].ParameterType;
                if (pt.IsValueType) il.InsertBefore(first, il.Create(OpCodes.Box, pt));
            }
            else il.InsertBefore(first, il.Create(OpCodes.Ldnull));
            il.InsertBefore(first, il.Create(OpCodes.Call, onSet));
            n++;
        }
        return n;
    }

    public static int PatchCalls(Program.Ctx c)
    {
        int n = 0;
        var coopActive = c.ShimMethod("Platform", "get_CoopActive");
        var call = c.ShimMethod("Platform", "Call", "System.Object", "System.String", "System.Object[]");
        var enter = c.ShimMethod("Platform", "CallEnter");
        var exit = c.ShimMethod("Platform", "CallExit");
        var objType = c.Mod.TypeSystem.Object;
        var seen = new HashSet<MethodDefinition>();
        foreach (var spec in Hooks)
        {
            string typeName = spec.Substring(0, spec.LastIndexOf('.'));
            string rest = spec.Substring(spec.LastIndexOf('.') + 1);
            int want = -1;
            if (rest.Contains('/')) { want = int.Parse(rest.Substring(rest.IndexOf('/') + 1)); rest = rest.Substring(0, rest.IndexOf('/')); }
            var t = c.Mod.GetType(typeName);
            var ms = t?.Methods.Where(x => x.Name == rest && x.HasBody && (want < 0 || x.Parameters.Count == want)).ToList();
            if (ms == null || ms.Count != 1) { c.Fail($"co-op hook {spec}: {(ms == null ? "type missing" : ms.Count + " matches")}"); continue; }
            var m = ms[0];
            if (!seen.Add(m)) { c.Fail($"co-op hook {spec}: listed twice"); continue; }
            var ret = m.ReturnType;
            bool isVoid = ret.FullName == "System.Void";
            bool refRet = !isVoid && !ret.IsValueType && !ret.IsGenericParameter;
            if (!isVoid && !refRet && ret.FullName != "System.Boolean" && ret.FullName != "System.Int32") { c.Fail($"co-op hook {spec}: returns {ret.FullName}"); continue; }
            if (m.Parameters.Any(p => p.ParameterType.IsByReference || p.ParameterType.ContainsGenericParameter)) { c.Fail($"co-op hook {spec}: unsupported parameters"); continue; }
            Wrap(m, isVoid);

            // prefix: if (Platform.CoopActive && Platform.Call(this, "T.M", new object[] { args })) return default;
            var body = m.Body;
            var il = body.GetILProcessor();
            var first = body.Instructions[0];  // Platform.CallEnter, added by Wrap
            void Emit(Instruction i) => il.InsertBefore(first, i);
            Emit(il.Create(OpCodes.Call, coopActive));
            Emit(il.Create(OpCodes.Brfalse, first));
            Emit(m.IsStatic ? il.Create(OpCodes.Ldnull) : il.Create(OpCodes.Ldarg_0));
            Emit(il.Create(OpCodes.Ldstr, typeName + "." + rest));
            Emit(il.Create(OpCodes.Ldc_I4, m.Parameters.Count));
            Emit(il.Create(OpCodes.Newarr, objType));
            for (int i = 0; i < m.Parameters.Count; i++)
            {
                var p = m.Parameters[i];
                Emit(il.Create(OpCodes.Dup));
                Emit(il.Create(OpCodes.Ldc_I4, i));
                Emit(il.Create(OpCodes.Ldarg, p));
                if (p.ParameterType.IsValueType) Emit(il.Create(OpCodes.Box, p.ParameterType));
                Emit(il.Create(OpCodes.Stelem_Ref));
            }
            Emit(il.Create(OpCodes.Call, call));
            Emit(il.Create(OpCodes.Brfalse, first));
            if (refRet) Emit(il.Create(OpCodes.Ldnull));             // skipped: no object
            else if (!isVoid) Emit(il.Create(OpCodes.Ldc_I4_0));
            Emit(il.Create(OpCodes.Ret));
            body.OptimizeMacros();
            n++;

            // body: CallEnter(); try { original } finally { CallExit(); }
            void Wrap(MethodDefinition md, bool voidRet)
            {
                var b = md.Body;
                b.SimplifyMacros();
                var p = b.GetILProcessor();
                VariableDefinition result = null;
                if (!voidRet) { result = new VariableDefinition(md.ReturnType); b.Variables.Add(result); b.InitLocals = true; }
                var end = voidRet ? p.Create(OpCodes.Ret) : p.Create(OpCodes.Ldloc, result);
                var finallyStart = p.Create(OpCodes.Call, exit);
                var endFinally = p.Create(OpCodes.Endfinally);
                var tryStart = b.Instructions[0];
                foreach (var ins in b.Instructions.ToList())
                {
                    if (ins.OpCode != OpCodes.Ret) continue;
                    if (voidRet) { ins.OpCode = OpCodes.Leave; ins.Operand = end; }
                    else
                    {
                        ins.OpCode = OpCodes.Stloc; ins.Operand = result;
                        p.InsertAfter(ins, p.Create(OpCodes.Leave, end));
                    }
                }
                p.Append(finallyStart);
                foreach (var h in b.ExceptionHandlers)
                {
                    // "to the end of the method" must not now take in the finally block
                    if (h.TryEnd == null) h.TryEnd = finallyStart;
                    if (h.HandlerEnd == null) h.HandlerEnd = finallyStart;
                }
                p.Append(endFinally);
                p.Append(end);
                if (!voidRet) p.Append(p.Create(OpCodes.Ret));
                b.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
                {
                    TryStart = tryStart, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = end,
                });
                p.InsertBefore(tryStart, p.Create(OpCodes.Call, enter));
            }
        }
        return n;
    }
}
