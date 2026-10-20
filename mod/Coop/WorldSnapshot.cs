using System;
using System.Collections.Generic;
using System.Reflection;
using ICS2Coop;
using ICS2Shim;
using LiteNetLib.Utils;
using UnityEngine;

namespace ICS2iOS
{
    // Joining: the host sends its whole world as a save (the game keeps everything in one FBPP key/value
    // store), the guest writes it into a separate session save file and loads the cafe from it. The guest's
    // own save file is never written while in a session; settings (language, graphics, volume ...) stay the
    // guest's own.
    public static class WorldSnapshot
    {
        public const string SessionSaveFile = "coop-session.save";

        public static bool InSessionSave => Platform.SaveFileName == SessionSaveFile;
        public static int ReceivedBytes { get; private set; }

        public static void Init()
        {
            CoopSession.Received += OnMessage;
            CoopSession.Net.PlayerJoined += (id, name) => { if (CoopSession.IsHost) SendTo(id); };
        }

        // ---- host -----------------------------------------------------------------------------------
        static void SendTo(int player)
        {
            string json = Build();
            if (json == null) return;
            var w = CoopSession.Net.Begin(Msg.WorldSnapshot);
            w.PutLargeString(json);
            CoopSession.Net.SendTo(player);
            Log.Info($"coop: sent world snapshot to player {player} ({json.Length / 1024} KB)");
        }

        /// The world as the game saves it, written into FBPP memory only (no file write, and without the
        /// daily side effects SaveManager.Save also runs: coin prices, employee days, party sounds ...).
        public static string Build()
        {
            var sm = SaveManager.Instance;
            var gm = GameManager.Instance;
            if (sm == null || gm == null) { Log.Warn("coop: snapshot asked for outside the cafe"); return null; }
            sm.enviro.Save();
            sm.inventory.SaveAllSlots();
            foreach (var s in sm.allSavers) s.Save();
            sm.tipBox.Save();
            foreach (var b in sm.zamazorBoxSaver) b.SaveZamazorBox();
            sm.reviewManager.Save();
            gm.Save();
            foreach (var e in gm.employeeManager.allEmployees)
            {
                // the saving half of Employee.DailyEmployeeCheckAndSave
                if (e.isRecruit && !e.fired) FBPP.SetString(e.employeName + "isrecruit", "recruit");
                else FBPP.DeleteKey(e.employeName + "isrecruit");
                if (e.isRecruit) FBPP.SetInt(e.employeName + "currentworkingday", e.currentWorkDay);
            }
            sm.crypto.SaveCoins();
            gm.billManager.Save();
            sm.cashRegister.Save();
            FBPP.SetString("gamesaved", "saved");
            foreach (var a in sm.abilitys) a.Save();
            sm.wife.Save();
            gm.homeFridge.Save();
            sm.boy.Save();
            gm.hobo.Save();
            sm.secondCafeManager.Save();
            return FBPP.GetSaveFileAsJson();
        }

        // ---- guest ----------------------------------------------------------------------------------
        static void OnMessage(int from, Msg id, NetDataReader r)
        {
            if (id != Msg.WorldSnapshot || CoopSession.IsHost) return;
            string json = r.GetLargeString();
            ReceivedBytes = json.Length;
            Log.Info($"coop: received world snapshot ({json.Length / 1024} KB)");
            if (!Apply(json)) { CoopSession.Leave(); return; }
            var menu = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
            if (menu != null) menu.LoadGame();
            else Log.Error("coop: snapshot arrived outside the main menu; cannot load the cafe");
        }

        /// Switches FBPP to the session file holding the host's world, keeping this device's settings.
        public static bool Apply(string json)
        {
            var model = JsonUtility.FromJson<Model>(json);
            if (model == null) { Log.Error("coop: world snapshot is not a save"); return false; }
            var own = JsonUtility.FromJson<Model>(FBPP.GetSaveFileAsJson()) ?? new Model();
            KeepLocal(model, own);
            Platform.SaveFileName = SessionSaveFile;
            Platform.IsCoopGuest = true;
            WorldSync.ResetGuest();
            FBPP.Start(Config());
            FBPP.OverwriteLocalSaveFile(JsonUtility.ToJson(model));
            Log.Info($"coop: playing in the host's world (session save '{SessionSaveFile}'); " +
                     $"money={FBPP.GetFloat("money")} day={FBPP.GetInt("globaldaycount")}");
            return true;
        }

        /// Back to this device's own save (session over).
        public static void Restore()
        {
            if (!InSessionSave) return;
            Platform.SaveFileName = Platform.DefaultSaveFileName;
            Platform.IsCoopGuest = false;
            FBPP.Start(Config());
            ForgetCache();
            s_ReloadOnMenu = true;
            Log.Info("coop: back to the own save file");
        }

        static bool s_ReloadOnMenu;

        /// Main menu after a session: reread the own save from disk once more, dropping anything the closing
        /// cafe scene wrote into memory after Restore (FBPP only writes to disk on an explicit Save).
        public static void ReloadAfterSession()
        {
            if (!s_ReloadOnMenu) return;
            s_ReloadOnMenu = false;
            FBPP.Start(Config());
            ForgetCache();
        }

        static FBPPConfig Config() => new FBPPConfig
        {
            // same settings as SaveManager / MainMenuManager use
            SaveFileName = Platform.SaveFileName,
            AutoSaveData = false,
            ScrambleSaveData = true,
            EncryptionSecret = "my-secret",
            SaveFilePath = Platform.PersistentDataPath,
        };

        /// FBPP keeps the loaded file in memory and FBPP.Start does not reread it.
        static void ForgetCache()
        {
            var f = typeof(FBPP).GetField("_latestData", BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) { Log.Error("coop: FBPP cache field not found; restart the game before playing solo"); return; }
            f.SetValue(null, null);
            FBPP.GetString("language"); // reload now
        }

        static bool IsLocalKey(string key) => key == "language" || key.StartsWith("option", StringComparison.Ordinal);

        static void KeepLocal(Model world, Model own)
        {
            world.StringData = Merge(world.StringData, own.StringData, x => x.Key);
            world.IntData = Merge(world.IntData, own.IntData, x => x.Key);
            world.FloatData = Merge(world.FloatData, own.FloatData, x => x.Key);
            world.BoolData = Merge(world.BoolData, own.BoolData, x => x.Key);
        }

        static T[] Merge<T>(T[] world, T[] own, Func<T, string> key)
        {
            var list = new List<T>();
            foreach (var x in world ?? new T[0]) if (!IsLocalKey(key(x))) list.Add(x);
            foreach (var x in own ?? new T[0]) if (IsLocalKey(key(x))) list.Add(x);
            return list.ToArray();
        }

        // mirror of FileBasedPrefs' internal FBPPFileModel (same JSON shape)
        [Serializable] public class StringItem { public string Key; public string Value; }
        [Serializable] public class IntItem { public string Key; public int Value; }
        [Serializable] public class FloatItem { public string Key; public float Value; }
        [Serializable] public class BoolItem { public string Key; public bool Value; }
        [Serializable]
        public class Model
        {
            public StringItem[] StringData = new StringItem[0];
            public IntItem[] IntData = new IntItem[0];
            public FloatItem[] FloatData = new FloatItem[0];
            public BoolItem[] BoolData = new BoolItem[0];
        }
    }
}
