using System;
using System.Text;
using System.Collections.Generic;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HKRL
{
    [BepInPlugin("hkrl.events.min3.udp", "HKRL Events (Minimal, UDP)", "0.7.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin I;
        private Harmony _harmony;

        // —— UDP 目标（记录/默认 + 环境变量可覆盖） —— //
        public const string UDP_HOST_DEFAULT = "127.0.0.1";
        public const int    UDP_PORT_DEFAULT = 28115;

        private string    _udpHost = UDP_HOST_DEFAULT;
        private int       _udpPort = UDP_PORT_DEFAULT;
        private UdpClient _udp;

        // —— 事件序号与场景 —— //
        private int _seq = 0;
        private string _scene = "";
        private readonly List<string> _scenes = new List<string>(4);

        // —— 目标类型 —— //
        private Type _tPlayerData;
        private Type _tHeroController;
        private Type _tHealthManager;

        // —— PlayerData 缓存 —— //
        private object _playerDataInst;
        private FieldInfo _pd_health;
        private FieldInfo _pd_healthBlue;
        private FieldInfo _pd_maxHealth;

        // —— 快照状态 —— //
        private PlayerSnap _player = new PlayerSnap();
        private readonly Dictionary<int, EnemySnap> _enemies = new Dictionary<int, EnemySnap>(128);

        private static readonly BindingFlags FI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        private static readonly BindingFlags FS = BindingFlags.Static   | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

        private void Awake()
        {
            I = this;
            DontDestroyOnLoad(this);

            // 1) UDP 初始化（环境变量可覆盖）
            InitUdp();

            // 2) 类型解析（PlayerData 明确名；其余宽松）
            _tPlayerData =
            AccessTools.TypeByName("PlayerData") ??
            AccessTools.TypeByName("GlobalSettings.PlayerData");
            _tHeroController = ResolveTypeBySimpleName("HeroController");
            _tHealthManager  = ResolveTypeBySimpleName("HealthManager");

            // 3) PlayerData 单例与字段（先属性后字段，忽略大小写）
            if (_tPlayerData != null)
            {
                _playerDataInst = GetStaticPropThenField(_tPlayerData, "instance") ?? GetStaticPropThenField(_tPlayerData, "Instance");
                _pd_health     = _tPlayerData.GetField("health", FI)     ?? AccessTools.Field(_tPlayerData, "health");
                _pd_healthBlue = _tPlayerData.GetField("healthBlue", FI) ?? AccessTools.Field(_tPlayerData, "healthBlue");
                _pd_maxHealth  = _tPlayerData.GetField("maxHealth", FI)  ?? AccessTools.Field(_tPlayerData, "maxHealth");
            }

            _harmony = new Harmony("hkrl.events.min3.udp");

            // 4) 敌人：注册/变化/消亡（所有同名重载统一打补丁）
            PatchAllInstanceByName(_tHealthManager, "Awake",      postfix: nameof(HM_OnEnable_Postfix));
            PatchAllInstanceByName(_tHealthManager, "OnEnable",   postfix: nameof(HM_OnEnable_Postfix));
            PatchAllInstanceByName(_tHealthManager, "TakeDamage", postfix: nameof(HM_OnDamaged_Postfix));
            PatchAllInstanceByName(_tHealthManager, "OnDisable",  postfix: nameof(HM_OnDisable_Postfix));
            PatchAllInstanceByName(_tHealthManager, "Die",        postfix: nameof(HM_OnDisable_Postfix));

            // 5) 玩家：掉血/加血
            PatchAllInstanceByName(_tHeroController, "OnEnable",   postfix: nameof(Player_OnEnable_Postfix));
            PatchAllInstanceByName(_tHeroController, "TakeDamage", postfix: nameof(Player_OnChanged_Postfix));
            PatchAllInstanceByName(_tHeroController, "AddHealth",  postfix: nameof(Player_OnChanged_Postfix));

            // 6) 场景事件
            SceneManager.sceneLoaded   += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            // 7) 初始化一次快照
            RefreshScenes();
            _scene = SceneManager.GetActiveScene().name ?? "";
            RefreshPlayerFromGame();
            RescanEnemiesAll();
            EmitSceneChanged();
        }

        private void OnDestroy()
        {
            try { SceneManager.sceneLoaded   -= OnSceneLoaded; } catch {}
            try { SceneManager.sceneUnloaded -= OnSceneUnloaded; } catch {}
            try { _harmony?.UnpatchSelf(); } catch {}
            try { _udp?.Close(); _udp = null; } catch {}
        }

        // —— 场景回调 —— //
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _scene = SceneManager.GetActiveScene().name ?? scene.name;
            RefreshScenes();
            RefreshPlayerFromGame();
            RescanEnemiesScene(scene);
            EmitSceneChanged();
        }
        private void OnSceneUnloaded(Scene scene)
        {
            RefreshScenes();
        }

        // —— 敌人 Hooks —— //
        private static void HM_OnEnable_Postfix(object __instance)
        {
            try { I?.OnEnemyEnable(__instance); } catch {}
        }
        private static void HM_OnDamaged_Postfix(object __instance)
        {
            try { I?.OnEnemyDamaged(__instance); } catch {}
        }
        private static void HM_OnDisable_Postfix(object __instance)
        {
            try { I?.OnEnemyDisable(__instance); } catch {}
        }

        private void OnEnemyEnable(object hm)
        {
            var go = GetGO(hm);
            if (go == null) return;

            int id = go.GetInstanceID();
            if (!_enemies.TryGetValue(id, out var snap))
            {
                snap = new EnemySnap { id = id, name = go.name ?? "Enemy", hp = 0 };
                _enemies[id] = snap;
            }
            int cur = ReadIntField(hm, "hp");
            if (cur >= 0) { snap.hp = cur; _enemies[id] = snap; }
        }

        private void OnEnemyDamaged(object hm)
        {
            var go = GetGO(hm);
            if (go == null) return;

            int id = go.GetInstanceID();
            if (!_enemies.TryGetValue(id, out var snap))
            {
                OnEnemyEnable(hm);
                if (!_enemies.TryGetValue(id, out snap)) return;
            }

            int prev = snap.hp;
            int cur = ReadIntField(hm, "hp");
            if (cur < 0) return;

            if (cur != prev)
            {
                snap.hp = cur; _enemies[id] = snap;
                EmitHpUpdate(new List<int>{ id });
            }
        }

        private void OnEnemyDisable(object hm)
        {
            var go = GetGO(hm);
            if (go == null) return;
            int id = go.GetInstanceID();

            if (_enemies.TryGetValue(id, out var snap))
            {
                if (snap.hp != 0)
                {
                    snap.hp = 0; _enemies[id] = snap;
                    EmitHpUpdate(new List<int>{ id });
                }
            }
        }

        // —— 玩家 Hooks —— //
        private static void Player_OnEnable_Postfix(object __instance)
        {
            try { I?.OnPlayerEnable(__instance); } catch {}
        }
        private static void Player_OnChanged_Postfix(object __instance)
        {
            try { I?.OnPlayerChanged(__instance); } catch {}
        }

        private void OnPlayerEnable(object hero)
        {
            var go = GetGO(hero);
            if (go != null) _player.id = go.GetInstanceID();
            RefreshPlayerFromGame();
        }

        private void OnPlayerChanged(object hero)
        {
            int p1 = _player.hp, p2 = _player.hp_blue, p3 = _player.hp_max;
            RefreshPlayerFromGame();
            if (_player.hp != p1 || _player.hp_blue != p2 || _player.hp_max != p3)
            {
                var changed = new List<int>();
                if (_player.id != 0) changed.Add(_player.id);
                EmitHpUpdate(changed);
            }
        }

        private void RefreshPlayerFromGame()
        {
            try
            {
                // 单例：先属性后字段，每次刷新都尝试重取
                if (_tPlayerData != null)
                {
                    var tryInst = GetStaticPropThenField(_tPlayerData, "instance") ?? GetStaticPropThenField(_tPlayerData, "Instance");
                    if (tryInst != null) _playerDataInst = tryInst;

                    if (_pd_health == null)     _pd_health     = _tPlayerData.GetField("health", FI)     ?? AccessTools.Field(_tPlayerData, "health");
                    if (_pd_healthBlue == null) _pd_healthBlue = _tPlayerData.GetField("healthBlue", FI) ?? AccessTools.Field(_tPlayerData, "healthBlue");
                    if (_pd_maxHealth == null)  _pd_maxHealth  = _tPlayerData.GetField("maxHealth", FI)  ?? AccessTools.Field(_tPlayerData, "maxHealth");
                }

                if (_playerDataInst != null)
                {
                    _player.hp      = ReadIntFieldCached(_playerDataInst, _pd_health);
                    _player.hp_blue = ReadIntFieldCached(_playerDataInst, _pd_healthBlue);
                    _player.hp_max  = ReadIntFieldCached(_playerDataInst, _pd_maxHealth);
                }

                if (_player.id == 0 && _tHeroController != null)
                {
                    var inst = GetStaticPropThenField(_tHeroController, "instance") ?? GetStaticPropThenField(_tHeroController, "Instance");
                    var go = GetGO(inst);
                    if (go != null) _player.id = go.GetInstanceID();
                }
            }
            catch {}
        }

        // —— 事件输出（日志 + UDP）—— //
        private void EmitHpUpdate(List<int> changedIds)
        {
            try
            {
                _seq++; long ts = UnixMillis();
                var sb = new StringBuilder(512);
                sb.Append("{");
                JPair(sb,"type","hp_update"); sb.Append(",");
                JPair(sb,"ts",ts); sb.Append(",");
                JPair(sb,"seq",_seq); sb.Append(",");
                JPair(sb,"scene",_scene); sb.Append(",");
                JArrayScenes(sb,"scenes",_scenes); sb.Append(",");

                sb.Append("\"player\":{");
                JPair(sb,"id",_player.id); sb.Append(",");
                JPair(sb,"hp",_player.hp); sb.Append(",");
                JPair(sb,"hp_blue",_player.hp_blue); sb.Append(",");
                JPair(sb,"hp_max",_player.hp_max);
                sb.Append("},");

                sb.Append("\"enemies\":[");
                bool first=true;
                foreach (var kv in _enemies)
                {
                    if (!first) sb.Append(",");
                    first=false;
                    var e = kv.Value;
                    sb.Append("{");
                    JPair(sb,"id",e.id); sb.Append(",");
                    JPair(sb,"name",e.name ?? "Enemy"); sb.Append(",");
                    JPair(sb,"hp",e.hp);
                    sb.Append("}");
                }
                sb.Append("],");

                sb.Append("\"changed\":{\"ids\":[");
                for (int i=0;i<changedIds.Count;i++){ if(i>0) sb.Append(","); sb.Append(changedIds[i]); }
                sb.Append("]}");

                sb.Append("}");
                string msg = sb.ToString();
                Logger.LogInfo(msg);
                UdpSend(msg);
            }
            catch {}
        }

        private void EmitSceneChanged()
        {
            try
            {
                _seq++; long ts = UnixMillis();
                var sb = new StringBuilder(512);
                sb.Append("{");
                JPair(sb,"type","scene_changed"); sb.Append(",");
                JPair(sb,"ts",ts); sb.Append(",");
                JPair(sb,"seq",_seq); sb.Append(",");
                JPair(sb,"scene",_scene); sb.Append(",");
                JArrayScenes(sb,"scenes",_scenes); sb.Append(",");

                sb.Append("\"snapshot\":{");
                sb.Append("\"player\":{");
                JPair(sb,"id",_player.id); sb.Append(",");
                JPair(sb,"hp",_player.hp); sb.Append(",");
                JPair(sb,"hp_blue",_player.hp_blue); sb.Append(",");
                JPair(sb,"hp_max",_player.hp_max);
                sb.Append("},");

                sb.Append("\"enemies\":[");
                bool first=true;
                foreach (var kv in _enemies)
                {
                    if (!first) sb.Append(",");
                    first=false;
                    var e = kv.Value;
                    sb.Append("{");
                    JPair(sb,"id",e.id); sb.Append(",");
                    JPair(sb,"name",e.name ?? "Enemy"); sb.Append(",");
                    JPair(sb,"hp",e.hp);
                    sb.Append("}");
                }
                sb.Append("]");

                sb.Append("}}");
                string msg = sb.ToString();
                Logger.LogInfo(msg);
                UdpSend(msg);
            }
            catch {}
        }

        // —— UDP —— //
        private void InitUdp()
        {
            try
            {
                var envHost = Environment.GetEnvironmentVariable("HKRL_UDP_HOST");
                var envPort = Environment.GetEnvironmentVariable("HKRL_UDP_PORT");

                if (!string.IsNullOrEmpty(envHost)) _udpHost = envHost;
                if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out var p) && p > 0 && p < 65536) _udpPort = p;

                _udp = new UdpClient();
                _udp.Connect(_udpHost, _udpPort);
                Logger.LogInfo($"[HKRL] UDP target {_udpHost}:{_udpPort}");
            }
            catch (Exception ex)
            {
                _udp = null;
                Logger.LogWarning($"[HKRL] UDP init failed: {ex.Message}");
            }
        }

        private void UdpSend(string s)
        {
            if (_udp == null || string.IsNullOrEmpty(s)) return;
            try
            {
                var data = Encoding.UTF8.GetBytes(s);
                _udp.Send(data, data.Length);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[HKRL] UDP send failed: {ex.Message}");
            }
        }

        // —— 扫描与工具 —— //
        private void RefreshScenes()
        {
            _scenes.Clear();
            int n = SceneManager.sceneCount;
            for (int i=0;i<n;i++){ var s=SceneManager.GetSceneAt(i); if (s.IsValid()) _scenes.Add(s.name ?? ""); }
        }
        private void RescanEnemiesAll()
        {
            _enemies.Clear();
            int n = SceneManager.sceneCount;
            for (int i=0;i<n;i++) RescanEnemiesScene(SceneManager.GetSceneAt(i));
        }
        private void RescanEnemiesScene(Scene scene)
        {
            if (_tHealthManager==null || !scene.IsValid()) return;
            try
            {
                var roots = scene.GetRootGameObjects();
                for (int i=0;i<roots.Length;i++)
                {
                    var comps = roots[i].GetComponentsInChildren(_tHealthManager, true);
                    for (int j=0;j<comps.Length;j++) OnEnemyEnable(comps[j]);
                }
            }
            catch {}
        }

        private Type ResolveTypeBySimpleName(string simple)
        {
            if (string.IsNullOrEmpty(simple)) return null;
            var t = AccessTools.TypeByName(simple);
            if (t != null) return t;
            string[] candidates = { simple, "GlobalSettings." + simple };
            foreach (var c in candidates)
            {
                t = AccessTools.TypeByName(c);
                if (t != null) return t;
            }
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i=0;i<asms.Length;i++)
                {
                    Type[] types;
                    try { types = asms[i].GetTypes(); } catch { continue; }
                    for (int j=0;j<types.Length;j++)
                    {
                        var tt = types[j];
                        if (tt != null && string.Equals(tt.Name, simple, StringComparison.Ordinal))
                            return tt;
                    }
                }
            }
            catch {}
            return null;
        }

        private object GetStaticPropThenField(Type t, string name)
        {
            try { var p = t.GetProperty(name, FS); if (p != null) return p.GetValue(null, null); } catch {}
            try { var f = t.GetField(name, FS);    if (f != null) return f.GetValue(null); } catch {}
            return null;
        }

        private static GameObject GetGO(object obj)
        {
            if (obj == null) return null;
            if (obj is GameObject go1) return go1;
            if (obj is Component c) return c.gameObject;
            try
            {
                var p = obj.GetType().GetProperty("gameObject", FI);
                if (p != null) return p.GetValue(obj, null) as GameObject;
            } catch {}
            return null;
        }

        private static int ReadIntField(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return -1;
            try
            {
                var fi = obj.GetType().GetField(name, FI);
                if (fi == null) return -1;
                return CoerceInt(fi.GetValue(obj));
            } catch {}
            return -1;
        }
        private static int ReadIntFieldCached(object inst, FieldInfo fi)
        {
            if (inst == null || fi == null) return -1;
            try { return CoerceInt(fi.GetValue(inst)); } catch { return -1; }
        }
        private static int CoerceInt(object v)
        {
            if (v == null) return -1;
            if (v is int vi) return vi;
            if (v is short vs) return vs;
            if (v is byte vb) return vb;
            if (v is long vl) return (int)vl;
            return -1;
        }

        private void PatchAllInstanceByName(Type t, string methodName, string prefix = null, string postfix = null)
        {
            if (t == null || string.IsNullOrEmpty(methodName)) return;
            try
            {
                var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var m in methods)
                {
                    if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                    HarmonyMethod pre = null, post = null;
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        var mi = AccessTools.Method(typeof(Plugin), prefix);
                        if (mi != null) pre = new HarmonyMethod(mi);
                    }
                    if (!string.IsNullOrEmpty(postfix))
                    {
                        var mi = AccessTools.Method(typeof(Plugin), postfix);
                        if (mi != null) post = new HarmonyMethod(mi);
                    }
                    if (pre != null || post != null) _harmony.Patch(m, pre, post);
                }
            }
            catch {}
        }

        private static long UnixMillis()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970,1,1)).TotalMilliseconds;
        }

        // —— JSON —— //
        private static void JPair(StringBuilder sb, string k, string v)
        {
            sb.Append("\""); JEsc(sb, k); sb.Append("\":\"");
            JEsc(sb, v ?? ""); sb.Append("\"");
        }

        private static void JPair(StringBuilder sb, string k, int v)
        {
            sb.Append("\""); JEsc(sb, k); sb.Append("\":");
            sb.Append(v);
        }

        private static void JPair(StringBuilder sb, string k, long v)
        {
            sb.Append("\""); JEsc(sb, k); sb.Append("\":");
            sb.Append(v);
        }

        private static void JArrayScenes(StringBuilder sb, string k, List<string> scenes)
        {
            sb.Append("\""); JEsc(sb, k); sb.Append("\":[");
            for (int i = 0; i < scenes.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\""); JEsc(sb, scenes[i] ?? ""); sb.Append("\"");
            }
            sb.Append("]");
        }

        private static void JEsc(StringBuilder sb, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4"));
                        }
                        else sb.Append(c);
                        break;
                }
            }
        }

        // —— 数据结构 —— //
        private struct PlayerSnap
        {
            public int id;
            public int hp;
            public int hp_blue;
            public int hp_max;
        }
        private struct EnemySnap
        {
            public int id;
            public string name;
            public int hp;
        }
    }
}
