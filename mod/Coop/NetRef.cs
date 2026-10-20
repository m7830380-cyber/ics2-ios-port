using System;
using System.Collections.Generic;
using System.Text;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ICS2iOS
{
    // Object references and plain values on the wire, for mirrored calls (CallSync) and NPCs (ActorSync).
    // A scene object is named by its path from the scene root, each step "name[k]" with k = its rank among
    // siblings of the same name (runtime-spawned "(Clone)" objects never shift it). Anything inside a world
    // item (ItemSync network id) is named from that item.
    public static class NetRef
    {
        const byte Null = 0, Int = 1, Float = 2, Bool = 3, Str = 4, Vec3 = 5, Quat = 6, Obj = 7, Long = 8, Dbl = 9;

        // ---- paths --------------------------------------------------------------------------------------------
        // whole paths of scene objects (not inside items), cached: they are asked for again and again
        sealed class CachedPath { public Transform T, Parent; public string Path; }
        static readonly Dictionary<int, CachedPath> s_PathCache = new Dictionary<int, CachedPath>();
        static GameObject[] s_Roots;
        static int s_RootsFrame = -1;

        public static void ClearCache() { s_PathCache.Clear(); s_Roots = null; }

        public static string PathOf(Transform t, Transform stopAt = null)
        {
            if (t == null) return "";
            int key = t.GetInstanceID();
            if (stopAt == null && s_PathCache.TryGetValue(key, out var hit) && hit.T == t && hit.Parent == t.parent) return hit.Path;
            var sb = new StringBuilder();
            for (var x = t; x != null && x != stopAt; x = x.parent) sb.Insert(0, "/" + x.name + "[" + RankAmongNamesakes(x) + "]");
            string path = sb.ToString();
            if (stopAt == null) s_PathCache[key] = new CachedPath { T = t, Parent = t.parent, Path = path };   // re-parented: made again
            return path;
        }

        static GameObject[] Roots(Scene scene)
        {
            if (s_Roots == null || s_RootsFrame != Time.frameCount) { s_Roots = scene.GetRootGameObjects(); s_RootsFrame = Time.frameCount; }
            return s_Roots;
        }

        static int RankAmongNamesakes(Transform t)
        {
            int k = 0;
            if (t.parent != null)
            {
                for (int i = 0; i < t.parent.childCount; i++)
                {
                    var s = t.parent.GetChild(i);
                    if (s == t) return k;
                    if (s.name == t.name) k++;
                }
                return k;
            }
            foreach (var root in Roots(t.gameObject.scene))
            {
                if (root.transform == t) return k;
                if (root.name == t.name) k++;
            }
            return k;
        }

        public static Transform Find(string path, Transform from = null)
        {
            if (string.IsNullOrEmpty(path)) return from;
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            Transform cur = from;
            foreach (var part in parts)
            {
                int b = part.LastIndexOf('[');
                string name = b >= 0 ? part.Substring(0, b) : part;
                int rank = b >= 0 ? int.Parse(part.Substring(b + 1, part.Length - b - 2)) : 0;
                Transform next = null;
                int k = 0;
                if (cur == null)
                {
                    foreach (var root in Roots(SceneManager.GetActiveScene()))
                        if (root.name == name && k++ == rank) { next = root.transform; break; }
                }
                else
                {
                    for (int i = 0; i < cur.childCount; i++)
                    {
                        var c = cur.GetChild(i);
                        if (c.name == name && k++ == rank) { next = c; break; }
                    }
                }
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        // ---- object references -------------------------------------------------------------------------------
        static readonly Dictionary<string, Type> s_Types = new Dictionary<string, Type>();

        public static Type TypeByName(string name)
        {
            if (s_Types.TryGetValue(name, out var t)) return t;
            t = typeof(GameManager).Assembly.GetType(name) ?? typeof(Component).Assembly.GetType(name) ?? Type.GetType(name);
            if (t == null)
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if ((t = asm.GetType(name)) != null) break;
            s_Types[name] = t;
            return t;
        }

        /// The item (network id) holding this transform, if any.
        static CoopItemId ItemOf(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                var id = t.GetComponent<CoopItemId>();
                if (id != null && id.Id > 0) return id;
            }
            return null;
        }

        static void PutRef(NetDataWriter w, UnityEngine.Object o)
        {
            GameObject go = o as GameObject ?? (o as Component)?.gameObject;
            var item = ItemOf(go.transform);
            if (item != null) { w.Put(item.Id); w.Put(PathOf(go.transform, item.transform)); }
            else { w.Put(0); w.Put(PathOf(go.transform)); }
            if (o is Component c)
            {
                var type = c.GetType();
                w.Put(type.FullName);
                var all = go.GetComponents(type);
                w.Put((byte)Mathf.Max(0, Array.IndexOf(all, c)));
            }
            else w.Put("");
        }

        static UnityEngine.Object GetRef(NetDataReader r)
        {
            int id = r.GetInt();
            string path = r.GetString();
            string typeName = r.GetString();
            int index = typeName.Length > 0 ? r.GetByte() : 0;
            Transform t;
            if (id > 0)
            {
                var root = ItemSync.Get(id);
                t = root != null ? Find(path, root.transform) : null;
            }
            else t = Find(path);
            if (t == null) { Log.Warn($"coop: object not found: {(id > 0 ? "item " + id : "")}{path}"); return null; }
            if (typeName.Length == 0) return t.gameObject;
            var type = TypeByName(typeName);
            if (type == null) { Log.Warn("coop: unknown type " + typeName); return null; }
            var all = t.GetComponents(type);
            return index < all.Length ? all[index] : null;
        }

        public static bool CanRef(UnityEngine.Object o) => o is GameObject || o is Component;

        public static void Put(NetDataWriter w, UnityEngine.Object o)
        {
            if (o == null || !CanRef(o)) { w.Put(false); return; }
            w.Put(true);
            PutRef(w, o);
        }

        public static UnityEngine.Object Get(NetDataReader r) => r.GetBool() ? GetRef(r) : null;

        // ---- values -------------------------------------------------------------------------------------------
        public static bool CanWrite(object v) =>
            v == null || v is int || v is float || v is bool || v is string || v is Vector3 || v is Quaternion || v is Enum ||
            v is long || v is double || v is short || v is byte || (v is UnityEngine.Object o && CanRef(o));

        public static void PutValue(NetDataWriter w, object v)
        {
            switch (v)
            {
                case null: w.Put(Null); break;
                case UnityEngine.Object o when o == null: w.Put(Null); break;   // destroyed object
                case Enum e: w.Put(Int); w.Put(Convert.ToInt32(e)); break;
                case int i: w.Put(Int); w.Put(i); break;
                case short s: w.Put(Int); w.Put((int)s); break;
                case byte b: w.Put(Int); w.Put((int)b); break;
                case float f: w.Put(Float); w.Put(f); break;
                case bool b: w.Put(Bool); w.Put(b); break;
                case string s: w.Put(Str); w.Put(s); break;
                case Vector3 p: w.Put(Vec3); w.Put(p.x); w.Put(p.y); w.Put(p.z); break;
                case Quaternion q: w.Put(Quat); w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w); break;
                case long l: w.Put(Long); w.Put(l); break;
                case double d: w.Put(Dbl); w.Put(d); break;
                case UnityEngine.Object o when CanRef(o): w.Put(Obj); PutRef(w, o); break;
                default: throw new ArgumentException("cannot send a " + v.GetType().Name);
            }
        }

        public static object GetValue(NetDataReader r)
        {
            switch (r.GetByte())
            {
                case Int: return r.GetInt();
                case Float: return r.GetFloat();
                case Bool: return r.GetBool();
                case Str: return r.GetString();
                case Vec3: return new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                case Quat: return new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
                case Long: return r.GetLong();
                case Dbl: return r.GetDouble();
                case Obj: return GetRef(r);
                default: return null;
            }
        }

        /// A received value as the parameter type the method wants.
        public static object As(object v, Type t)
        {
            if (v == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
            if (t.IsInstanceOfType(v)) return v;
            if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt32(v));
            if (v is GameObject go && typeof(Component).IsAssignableFrom(t)) return go.GetComponent(t);
            if (v is Component c)
            {
                if (t == typeof(GameObject)) return c.gameObject;
                if (typeof(Component).IsAssignableFrom(t)) return c.GetComponent(t);
            }
            if (v is IConvertible && typeof(IConvertible).IsAssignableFrom(t)) return Convert.ChangeType(v, t);
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }
    }
}
