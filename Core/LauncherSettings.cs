using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace KytyLauncher.Core
{
    /// <summary>
    /// launcher-settings.json, shared with launcher.ps1: the same keys and value types, so either launcher can be used.
    /// Keys this launcher does not know are kept as they were.
    /// </summary>
    public sealed class LauncherSettings
    {
        readonly string path;
        readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);

        // The defaults of launcher.ps1, in its order.
        static readonly (string Key, object Value)[] Defaults =
        {
            ("game", ""), ("resolution", "2560x1440"), ("fullscreen", false), ("aspect", true), ("language", 1),
            ("redzone", true), ("ecores", false), ("fps120", false), ("render", 1440), ("ratio", "16:9"),
            ("quality", "standard"), ("vsync", false), ("hdr", false), ("framegen", 0), ("hud", 0), ("streamline", ""),
        };

        public LauncherSettings(string root)
        {
            path = Path.Combine(root, "launcher-settings.json");
            foreach (var d in Defaults) values[d.Key] = d.Value;
            try
            {
                if (File.Exists(path))
                {
                    var saved = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                    if (saved != null) foreach (var kv in saved) values[kv.Key] = kv.Value;
                }
            }
            catch { }
            if (string.IsNullOrWhiteSpace(Game))
            {
                var remembered = Path.Combine(root, "game-path.txt");
                try { if (File.Exists(remembered)) Game = File.ReadLines(remembered).FirstOrDefault()?.Trim() ?? ""; } catch { }
            }
        }

        public bool Has(string key) => values.ContainsKey(key);

        string Str(string key) => values.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) : "";
        bool Bool(string key)
        {
            if (!values.TryGetValue(key, out var v) || v == null) return false;
            if (v is bool b) return b;
            try { return Convert.ToInt32(v) != 0; } catch { return false; }
        }
        int Int(string key)
        {
            if (!values.TryGetValue(key, out var v) || v == null) return 0;
            if (v is bool b) return b ? 1 : 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }

        public string Game { get => Str("game"); set => values["game"] = value ?? ""; }
        public string Resolution { get => Str("resolution"); set => values["resolution"] = value; }
        public bool Fullscreen { get => Bool("fullscreen"); set => values["fullscreen"] = value; }
        public bool Aspect { get => Bool("aspect"); set => values["aspect"] = value; }
        public int Language { get => Int("language"); set => values["language"] = value; }
        public bool RedZone { get => Bool("redzone"); set => values["redzone"] = value; }
        public bool ECores { get => Bool("ecores"); set => values["ecores"] = value; }
        public bool Fps120 { get => Bool("fps120"); set => values["fps120"] = value; }
        public int Render { get => Int("render"); set => values["render"] = value; }
        public string Ratio { get => Str("ratio"); set => values["ratio"] = value; }
        public string Quality { get => Str("quality"); set => values["quality"] = value; }
        public bool VSync { get => Bool("vsync"); set => values["vsync"] = value; }
        public bool Hdr { get => Bool("hdr"); set => values["hdr"] = value; }
        public int FrameGen { get => Int("framegen"); set => values["framegen"] = value; }
        public int Hud { get => Int("hud"); set => values["hud"] = value; }
        public string Streamline { get => Str("streamline"); set => values["streamline"] = value ?? ""; }

        // The Demon's Souls build's launcher.ps1 has it too (same key).
        public bool SafeGpu { get => Bool("safegpu"); set => values["safegpu"] = value; }

        // This launcher's own options (launcher.ps1 ignores them).
        public bool CloseOnPlay
        {
            get => !values.ContainsKey("closeOnPlay") || Bool("closeOnPlay");
            set => values["closeOnPlay"] = value;
        }

        bool BoolOr(string key, bool fallback) => values.ContainsKey(key) ? Bool(key) : fallback;
        int IntOr(string key, int fallback) => values.ContainsKey(key) ? Int(key) : fallback;

        // A game's options of the upstream launcher (src/launcher, KytyPS5 0.3.0), given where the build takes them.
        public bool AmdCpu { get => BoolOr("amdcpu", EmulatorInstall.IsAmdCpu); set => values["amdcpu"] = value; }
        public bool Tessellation { get => Bool("tessellation"); set => values["tessellation"] = value; }
        public bool ReadbackLinear { get => Bool("readbacklinear"); set => values["readbacklinear"] = value; }
        public bool SyncRawImages { get => Bool("syncraw"); set => values["syncraw"] = value; }
        public bool ZMask { get => Bool("zmask"); set => values["zmask"] = value; }
        public bool SkipLogos { get => Bool("skiplogos"); set => values["skiplogos"] = value; }
        public string AddCont { get => Str("addcont"); set => values["addcont"] = value ?? ""; }
        public string GamePatch { get => Str("gamepatch"); set => values["gamepatch"] = value ?? ""; }
        // KytyPS5 zipeek: compute shaders from the game's executable, buffer strides read at run time.
        public bool EbootPrecompile { get => Bool("ebootprecompile"); set => values["ebootprecompile"] = value; }

        // The launcher's options for every game (the upstream launcher's profile, controller, notifications, debug).
        public string UserName { get => Str("username"); set => values["username"] = value ?? ""; }
        public int UserId { get => IntOr("userid", 1000); set => values["userid"] = value; }
        /// <summary>The Vulkan device index; -1 the emulator's choice.</summary>
        public int Gpu { get => IntOr("gpu", -1); set => values["gpu"] = value; }
        /// <summary>The DualSense lightbar #RRGGBB; empty: the game sets it.</summary>
        public string Lightbar { get => Str("lightbar"); set => values["lightbar"] = value ?? ""; }
        public int Vibration { get => IntOr("vibration", 100); set => values["vibration"] = value; }
        public int SpeakerVolume { get => IntOr("speakervolume", 50); set => values["speakervolume"] = value; }
        public int KeyboardPlayer { get => Int("keyboardplayer"); set => values["keyboardplayer"] = value; }
        public bool Network { get => Bool("network"); set => values["network"] = value; }
        public bool Trophies { get => BoolOr("trophies", true); set => values["trophies"] = value; }
        public int TrophyDuration { get => IntOr("trophyduration", 6); set => values["trophyduration"] = value; }
        public bool HideCursor { get => Bool("hidecursor"); set => values["hidecursor"] = value; }
        public bool VulkanValidation { get => Bool("vulkanvalidation"); set => values["vulkanvalidation"] = value; }
        public bool ShaderValidation { get => Bool("shadervalidation"); set => values["shadervalidation"] = value; }
        public bool RenderDoc { get => Bool("renderdoc"); set => values["renderdoc"] = value; }
        public bool CheckUpdates { get => BoolOr("checkUpdates", true); set => values["checkUpdates"] = value; }
        public bool PrecompilePrompt { get => BoolOr("precompilePrompt", true); set => values["precompilePrompt"] = value; }

        /// <summary>The options that belong to a game (its Game Settings); the rest (game, ecores, streamline, closeOnPlay,
        /// the profile, controller, notifications and debug) are the launcher's.</summary>
        public static readonly string[] GameKeys =
        {
            "resolution", "fullscreen", "aspect", "language", "redzone", "fps120", "vsync", "hdr", "render", "ratio", "quality", "framegen", "hud",
            "safegpu", "amdcpu", "tessellation", "readbacklinear", "syncraw", "zmask", "skiplogos", "addcont", "gamepatch", "ebootprecompile",
        };

        // The defaults of the newer game keys (not in launcher.ps1's list).
        static readonly (string Key, object Value)[] GameDefaults =
        {
            ("safegpu", false), ("tessellation", false), ("readbacklinear", false), ("syncraw", false), ("zmask", false),
            ("skiplogos", false), ("addcont", ""), ("gamepatch", ""), ("ebootprecompile", false),
        };

        public Dictionary<string, object> ExportGame() => GameKeys.ToDictionary(k => k, k => values.TryGetValue(k, out var v) ? v : null);

        public void ImportGame(IDictionary<string, object> game)
        {
            // A game saved before a key existed takes its default (not the previous game's value).
            ResetGame();
            foreach (var k in GameKeys)
                if (game.TryGetValue(k, out var v) && v != null) values[k] = v;
        }

        public void ResetGame()
        {
            foreach (var d in Defaults) if (GameKeys.Contains(d.Key)) values[d.Key] = d.Value;
            foreach (var d in GameDefaults) values[d.Key] = d.Value;
            values.Remove("amdcpu"); // the CPU's vendor decides
        }

        public void Save()
        {
            try { File.WriteAllText(path, Json.Pretty(values), new UTF8Encoding(true)); } catch { }
        }
    }

    /// <summary>Indented JSON in the style of PowerShell's ConvertTo-Json, readable by both launchers.</summary>
    public static class Json
    {
        public static string Pretty(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value, int indent)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case string s: sb.Append(new JavaScriptSerializer().Serialize(s)); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case IDictionary<string, object> map:
                    if (map.Count == 0) { sb.Append("{}"); break; }
                    sb.Append("{\n");
                    var i = 0;
                    foreach (var kv in map)
                    {
                        sb.Append(' ', indent + 4).Append(new JavaScriptSerializer().Serialize(kv.Key)).Append(": ");
                        Write(sb, kv.Value, indent + 4);
                        if (++i < map.Count) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(' ', indent).Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    var items = list.Cast<object>().ToList();
                    if (items.Count == 0) { sb.Append("[]"); break; }
                    sb.Append("[\n");
                    for (var j = 0; j < items.Count; j++)
                    {
                        sb.Append(' ', indent + 4);
                        Write(sb, items[j], indent + 4);
                        if (j + 1 < items.Count) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(' ', indent).Append(']');
                    break;
                default:
                    sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }
    }
}
