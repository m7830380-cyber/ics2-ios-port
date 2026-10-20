using System.Globalization;
using System.Text;
using DG.Tweening;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // Test-only (-ics2test): what the headless bot (coop/bot) can read and do inside a real game, so co-op is
    // checked by scripts instead of playtests. Reports are key=value lines; values use the invariant culture.
    public static class CoopTestAgent
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Report(StringBuilder sb, string detail = "")
        {
            var net = CoopSession.Net;
            Line(sb, "scene", SceneManager.GetActiveScene().name);
            Line(sb, "coop.role", !CoopSession.Active ? "off" : CoopSession.IsHost ? "host" : "client");
            Line(sb, "coop.localId", net.LocalId.ToString(Inv));
            Line(sb, "coop.players", net.PlayerCount.ToString(Inv));
            Line(sb, "coop.names", string.Join(",", Names(net)));
            Line(sb, "save.file", ICS2Shim.Platform.SaveFileName);
            var savePath = System.IO.Path.Combine(ICS2Shim.Platform.PersistentDataPath, ICS2Shim.Platform.SaveFileName);
            Line(sb, "save.mtime", System.IO.File.Exists(savePath) ? System.IO.File.GetLastWriteTimeUtc(savePath).Ticks.ToString(Inv) : "0");
            Line(sb, "fbpp.language", FBPP.GetString("language"));
            Line(sb, "snapshot.received", WorldSnapshot.ReceivedBytes.ToString(Inv));
            Line(sb, "fps",(1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f)).ToString("0", Inv));
            var gm = GameManager.Instance;
            Line(sb, "game.ready", (gm != null).ToString());
            if (gm != null)
            {
                Line(sb, "game.money", gm.money.ToString("0.##", Inv));
                Line(sb, "game.day", gm.day.ToString(Inv));
                Line(sb, "game.paused", gm.gamePaused.ToString());
                if (gm.player != null) Line(sb, "player.pos", Vec(gm.player.position));
            }
            if (EnviroSky.instance != null)
                Line(sb, "time.hours", EnviroSky.instance.GameTime.Hours.ToString(Inv));
            PlayerSync.Report(sb);
            WorldSync.Report(sb);
            ItemSync.Report(sb, Detail(detail, "items"));
            CustomerSync.Report(sb, Detail(detail, "civs"));
            ActorSync.Report(sb, Detail(detail, "actors"));
            CallSync.Report(sb);
            PrefSync.Report(sb);
            StateWatch.Report(sb);
            Line(sb, "items.tableTag", TableTag());
            Line(sb, "items.any", ItemSync.AnyWorldItemId().ToString(Inv));
            if (EnviroSky.instance != null) Line(sb, "time.hourf", EnviroSky.instance.internalHour.ToString("0.000", Inv));
            WorldReport(sb);
        }

        /// The shared world at a glance: doors, bills, rooms bought, a test progress key, weather, animals.
        static void WorldReport(StringBuilder sb)
        {
            var gm = GameManager.Instance;
            if (gm == null) return;
            var doors = Sorted(Object.FindObjectsOfType<Door>(true));
            var bits = new StringBuilder();
            foreach (var d in doors) bits.Append(d.doorAnim != null && d.doorAnim.isActiveAndEnabled && d.doorAnim.GetBool("open") ? '1' : '0');
            Line(sb, "world.doors", bits.ToString());
            int usable = System.Array.FindIndex(doors, d => d.doorAnim != null && d.doorAnim.isActiveAndEnabled && !d.doorLock && !d.closecafe);
            Line(sb, "world.doorpick", usable.ToString(Inv));
            Line(sb, "world.bills", gm.billManager != null ? gm.billManager.bills.Count.ToString(Inv) : "-1");
            bits.Clear();
            foreach (var u in Sorted(Object.FindObjectsOfType<UpgradeItem>(true))) bits.Append(u.buyed ? '1' : '0');
            Line(sb, "world.upgrades", bits.ToString());
            Line(sb, "world.testpref", FBPP.GetString("cooptestkey"));
            var e = EnviroSky.instance;
            Line(sb, "world.weather", e != null && e.Weather != null && e.Weather.currentActiveWeatherPreset != null ? e.Weather.currentActiveWeatherPreset.Name : "");
            bits.Clear();
            foreach (var c in Sorted(Object.FindObjectsOfType<Cat>(true))) bits.Append(c.gameObject.activeInHierarchy ? '1' : '0');
            bits.Append('/');
            foreach (var c in Sorted(Object.FindObjectsOfType<Dog>(true))) bits.Append(c.gameObject.activeInHierarchy ? '1' : '0');
            Line(sb, "world.animals", bits.ToString());
            Line(sb, "world.tipbox", SaveManager.Instance != null && SaveManager.Instance.tipBox != null ? SaveManager.Instance.tipBox.tipboxmoney.ToString("0.##", Inv) : "");
            Line(sb, "player.atpc", (gm.computer != null && gm.computer.playerComputer != null && gm.computer.playerComputer.enterPc).ToString());
        }

        static T[] Sorted<T>(T[] list) where T : Component
        {
            System.Array.Sort(list, (a, b) => string.CompareOrdinal(NetRef.PathOf(a.transform), NetRef.PathOf(b.transform)));
            return list;
        }

        /// "items=1,2;civs=3" -> Detail(.., "civs") = "3"
        static string Detail(string detail, string key)
        {
            foreach (var part in (detail ?? "").Split(';'))
                if (part.StartsWith(key + "=")) return part.Substring(key.Length + 1);
            return "";
        }

        static string TableTag()
        {
            if (GameManager.Instance == null) return "";
            foreach (var p in GameManager.Instance.allGamePrefabs)
                if (p != null && p.GetComponent<TableController>() != null) return p.myItem.itemTag;
            return "";
        }

        public static void Run(int from, string command, string arg)
        {
            Log.Info($"test: command '{command}' '{arg}' from player {from}");
            switch (command)
            {
                case "quit":
                    Application.Quit();
                    break;
                case "log":
                    Log.Info("test: " + arg);
                    break;
                case "money":
                    // what every money change in the game does: edit the field directly
                    GameManager.Instance.money += float.Parse(arg, Inv);
                    break;
                case "take":
                {
                    // what pressing E on an item does
                    var go = ItemSync.Get(int.Parse(arg, Inv));
                    if (go != null) GameManager.Instance.playerRay.TakeItem(go.GetComponent<Item>());
                    break;
                }
                case "build":
                {
                    // aim at a spot and click: the item is moved there by the build preview, then built
                    var ray = GameManager.Instance.playerRay;
                    if (ray.takedItem == null) { Log.Warn("test: build: nothing in hand"); break; }
                    var p = arg.Split(',');
                    ray.takedItem.transform.DOKill();
                    ray.takedItem.transform.position = new Vector3(float.Parse(p[0], Inv), float.Parse(p[1], Inv), float.Parse(p[2], Inv));
                    ray.Build(ray.takedItem);
                    break;
                }
                case "spawnitem":
                {
                    // what opening a delivery box does (PlayerRay, CargoBox branch)
                    var parts = arg.Split('@');
                    var p = parts[1].Split(',');
                    foreach (var prefab in GameManager.Instance.allGamePrefabs)
                    {
                        if (prefab.myItem.itemTag != parts[0]) continue;
                        var go = Object.Instantiate(prefab.gameObject, new Vector3(float.Parse(p[0], Inv), float.Parse(p[1], Inv), float.Parse(p[2], Inv)), Quaternion.identity);
                        go.name += "TEST";
                        foreach (var s in SaveManager.Instance.allSavers) if (s.item == null) { s.AddItem(go.GetComponent<Item>()); break; }
                        break;
                    }
                    break;
                }
                case "destroyitem":
                {
                    var go = ItemSync.Get(int.Parse(arg, Inv));
                    if (go != null)
                    {
                        var item = go.GetComponent<Item>();
                        if (item != null && item.mySaver != null) item.mySaver.DeleteItem();
                        Object.Destroy(go);
                    }
                    break;
                }
                case "order":
                {
                    // put one item in the in-game shop basket and pay (ZamazorManager, as the shop page does)
                    var zm = GameManager.Instance.zamazorManager;
                    zm.AddMyItemToBasket(GameManager.Instance.GiveMeItemObject(arg));
                    zm.CompletePayment();
                    break;
                }
                case "forcepay":
                {
                    // host test: customer <i> waits to pay <price>
                    var p = arg.Split(' ');
                    var c = GameManager.Instance.civilManager.allCivils[int.Parse(p[0], Inv)];
                    c.gameObject.SetActive(true);
                    c.myEnum = CivilEnum.PayTime;
                    c.payedPrice = float.Parse(p[1], Inv);
                    break;
                }
                case "takecash":
                {
                    // what pressing E on a paying customer does (PlayerRay)
                    var c = GameManager.Instance.civilManager.allCivils[int.Parse(arg, Inv)];
                    if (c.myEnum == CivilEnum.PayTime && c.payedPrice > 0f)
                    {
                        GameManager.Instance.OpenMoneyAddedUI(c.payedPrice);
                        GameManager.Instance.money += c.payedPrice;
                        c.TakedMoney();
                    }
                    break;
                }
                case "notify":
                {
                    // host test: a customer asks for the PC at table <id>
                    var t = ItemSync.Get(int.Parse(arg, Inv))?.GetComponent<TableController>();
                    if (t != null) GameManager.Instance.cafeSoftManager.GenerateNotification(t, "PC 1", 1f);
                    break;
                }
                case "approve":
                {
                    // the Approve button in the CafeSoft app (first open request)
                    int found = 0;
                    foreach (var t in Object.FindObjectsOfType<TableController>())
                        if (t.myCustomerRequest != null)
                        {
                            found++;
                            Log.Info($"test: approving the request on '{t.name}' (table '{t.myCustomerRequest.myTable?.name}')");
                            t.myCustomerRequest.Approved();
                            break;
                        }
                    if (found == 0) Log.Warn("test: approve: no open request");
                    break;
                }
                case "screenshot":
                    // the game's own capture of its window (nothing outside the game can be in it)
                    CoopSession.Instance.StartCoroutine(Screenshot(arg));
                    break;
                case "panel":
                    CoopPanel.Open = arg != "close";
                    break;
                case "sleep":
                    // what going to bed does after the fade (PlayerRay -> SaveManager.SleepingChanges)
                    SaveManager.Instance.SleepingChanges();
                    break;
                case "dailychanges":
                    GameManager.Instance.DailyChanges();
                    break;
                case "tp":
                {
                    // move the player ("x,y,z"), for looking at places (the sky outside)
                    var v = arg.Split(',');
                    var pl = GameManager.Instance.player;
                    var cc = pl.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = false;
                    pl.position = new Vector3(float.Parse(v[0], Inv), float.Parse(v[1], Inv), float.Parse(v[2], Inv));
                    if (cc != null) cc.enabled = true;
                    break;
                }
                case "look":
                {
                    // camera pitch in degrees (negative: up)
                    var cam = GameManager.Instance.playerCamera;
                    if (cam != null) cam.transform.localRotation = Quaternion.Euler(float.Parse(arg, Inv), 0f, 0f);
                    break;
                }
                case "sky":
                    IosSettings.SimpleSky = arg == "simple";
                    IosSettings.Save();
                    break;
                case "shotpuppet":
                    ShotPuppet(arg);
                    break;
                case "lights":
                {
                    // the light switch (PlayerRay "lightswitch")
                    var ls = GameManager.Instance.lightSwitch;
                    if (ls.lightsOn) ls.CloseLight(); else ls.OpenLight();
                    break;
                }
                case "power":
                {
                    var es = GameManager.Instance.electricalSwitch;
                    if (es.electric) es.SwitchClosed(); else es.SwitchOpened();
                    break;
                }
                case "door":
                {
                    // PlayerRay "door" / "garagedoor": open or close by the animator's state
                    var d = Sorted(Object.FindObjectsOfType<Door>(true))[int.Parse(arg, Inv)];
                    if (d.doorAnim.GetBool("open")) d.CloseDoor(); else d.OpenDoor();
                    break;
                }
                case "pref":
                    FBPP.SetString("cooptestkey", arg);
                    break;
                case "dirt":
                    for (int i = 0; i < int.Parse(arg, Inv); i++) GameManager.Instance.footstepsManager.RandomSpawnTrash();
                    break;
                case "mop":
                {
                    // the mop's sweep (PlayerRay.SweepCompleted -> FootstepsManager.DestroyMe) on the first bit of dirt
                    var fm = GameManager.Instance.footstepsManager;
                    foreach (var area in fm.footprintsSpawnAreas)
                        foreach (var go in area.SpawnedDirtyObjects)
                            if (go != null && go.GetComponent<Trash>() != null) { fm.DestroyMe(go.GetComponent<Trash>()); return; }
                    Log.Warn("test: mop: no dirt");
                    break;
                }
                case "bill":
                    GameManager.Instance.billManager.SpawnNewBill("Co-op test", float.Parse(arg, Inv), 5);
                    break;
                case "paybill":
                {
                    var bm = GameManager.Instance.billManager;
                    if (bm.bills.Count > 0) bm.bills[bm.bills.Count - 1].PayBtn();
                    else Log.Warn("test: paybill: no bill");
                    break;
                }
                case "upgrade":
                {
                    // the cheapest room not bought yet (PC app "My cafe" > Buy)
                    UpgradeItem best = null;
                    foreach (var u in Object.FindObjectsOfType<UpgradeItem>(true))
                        if (!u.buyed && u.type != "texture" && (best == null || u.price < best.price)) best = u;
                    if (best == null) { Log.Warn("test: upgrade: nothing to buy"); break; }
                    Log.Info($"test: buying upgrade {best.number} for {best.price}");
                    best.Buy();
                    break;
                }
                case "elevator":
                {
                    // a "go to" button of the first elevator: the one whose floor is farthest from the car
                    var buttons = Object.FindObjectsOfType<OldElevator.ElevatorButton>(true);
                    System.Array.Sort(buttons, (a, b) => string.CompareOrdinal(NetRef.PathOf(a.transform), NetRef.PathOf(b.transform)));
                    var b0 = buttons.Length > int.Parse(arg, Inv) ? buttons[int.Parse(arg, Inv)] : null;
                    if (b0 == null) { Log.Warn("test: elevator: no such button"); break; }
                    Log.Info($"test: pressing elevator button '{b0.name}' ({b0.buttonType})");
                    b0.Press();
                    break;
                }
                case "elevatorbuttons":
                {
                    var buttons = Object.FindObjectsOfType<OldElevator.ElevatorButton>(true);
                    System.Array.Sort(buttons, (a, b) => string.CompareOrdinal(NetRef.PathOf(a.transform), NetRef.PathOf(b.transform)));
                    for (int i = 0; i < buttons.Length; i++)
                        Log.Info($"test: elevator button {i}: '{NetRef.PathOf(buttons[i].transform)}' {buttons[i].buttonType} -> {(buttons[i].destination != null ? buttons[i].destination.position.y.ToString("0.00", Inv) : "-")}");
                    break;
                }
                case "pc":
                {
                    // E on the own PC (PlayerRay "playerComputer"), Q to leave
                    var pc = GameManager.Instance.computer.playerComputer;
                    var ray = GameManager.Instance.playerRay;
                    if (arg == "enter") { ray.currentPlayerComputer = pc; pc.EnterComputer(); }
                    else pc.ExitComputer();
                    break;
                }
                case "punchciv":
                {
                    // a fist lands on customer <i> (the guest's punch collider entering its trigger)
                    var c = GameManager.Instance.civilManager.allCivils[int.Parse(arg, Inv)];
                    var punch = GameManager.Instance.playerRay.allPunch[0].GetComponent<Collider>();
                    typeof(Civil).GetMethod("OnTriggerEnter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .Invoke(c, new object[] { punch });
                    break;
                }
                default:
                    Log.Warn($"test: unknown command '{command}'");
                    break;
            }
        }

        /// Renders the first puppet from 2.5 m in front with a temporary camera into <profile>/<file>.png.
        /// The game draws the picture itself: nothing outside the game window can end up in it.
        static void ShotPuppet(string file)
        {
            var body = PlayerSync.FirstPuppetBody();
            if (body == null) { Log.Warn("test: shotpuppet: no puppet"); return; }
            var go = new GameObject("CoopTestCamera");
            try
            {
                var cam = go.AddComponent<Camera>();
                var main = GameManager.Instance != null ? GameManager.Instance.playerCamera : Camera.main;
                if (main != null) { cam.CopyFrom(main); cam.RemoveAllCommandBuffers(); }
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.4f, 0.5f, 0.6f);
                cam.renderingPath = RenderingPath.Forward;
                var t = body.transform;
                go.transform.position = t.position + t.forward * 2.5f + Vector3.up * 1.4f;
                go.transform.LookAt(t.position + Vector3.up * 0.9f);
                // only the puppet (moved to a spare layer while rendering), so walls can't hide it
                const int Layer = 31;
                var layers = new System.Collections.Generic.Dictionary<GameObject, int>();
                foreach (var tr in body.GetComponentsInChildren<Transform>(true)) { layers[tr.gameObject] = tr.gameObject.layer; tr.gameObject.layer = Layer; }
                cam.cullingMask = 1 << Layer;
                cam.nearClipPlane = 0.05f;
                var rt = new RenderTexture(640, 360, 24);
                cam.targetTexture = rt;
                cam.Render();
                foreach (var kv in layers) kv.Key.layer = kv.Value;
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(640, 360, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                cam.targetTexture = null;
                string path = System.IO.Path.Combine(ICS2Shim.Platform.PersistentDataPath, System.IO.Path.GetFileName(file));
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.Destroy(tex);
                rt.Release();
                Object.Destroy(rt);
                Log.Info("test: puppet picture saved to " + path);
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        static System.Collections.IEnumerator Screenshot(string file)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            string path = System.IO.Path.Combine(ICS2Shim.Platform.PersistentDataPath, System.IO.Path.GetFileName(file));
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex);
            Log.Info("test: screenshot saved to " + path);
        }

        static System.Collections.Generic.IEnumerable<string> Names(ICS2Coop.CoopNet net)
        {
            foreach (int id in net.Players) yield return id + ":" + net.NameOf(id);
        }

        static string Vec(Vector3 v) => v.x.ToString("0.00", Inv) + "," + v.y.ToString("0.00", Inv) + "," + v.z.ToString("0.00", Inv);

        static void Line(StringBuilder sb, string key, string value) => sb.Append(key).Append('=').Append(value).Append('\n');
    }
}
