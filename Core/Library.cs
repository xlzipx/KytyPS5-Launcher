using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KytyLauncher.Core
{
    public enum ShaderState { Unknown, Checking, Ready, Outdated, Missing, Recorded, NotRecorded }

    /// <summary>A game of the library: its folder (eboot.bin, sce_sys) or a .zar archive of that folder.</summary>
    public sealed class GameEntry : INotifyPropertyChanged
    {
        public GameEntry(string path) { Path = path; }

        public string Path { get; }
        public DateTime LastPlayed { get; set; }
        /// <summary>This game's own options (LauncherSettings.GameKeys); null until it is first selected.</summary>
        public Dictionary<string, object> Settings { get; set; }
        /// <summary>The folder of the KytyPS5 build this game runs on; "" for the default (Demon's Souls: this launcher's
        /// build; another game: the first other build added, else this one).</summary>
        public string Emulator { get; set; } = "";
        public bool IsZar => Path.EndsWith(".zar", StringComparison.OrdinalIgnoreCase);
        /// <summary>Demon's Souls (any region: PPSA01340-PPSA01342), the game this build is tuned for: its static shader
        /// precompile and its render options (render height, aspect ratio, graphics) work only for it, as in run-windows.ps1.
        /// While the title is not known (a .zar read without the fork's tool), the folder or file name decides.</summary>
        public bool IsDemonsSouls => string.IsNullOrEmpty(TitleId)
            ? System.Text.RegularExpressions.Regex.IsMatch(Path, "PPSA0134[0-2]|Demon.?s.?Souls", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : System.Text.RegularExpressions.Regex.IsMatch(TitleId, "^PPSA0134[0-2]$");

        public bool Exists => IsZar ? File.Exists(Path) : File.Exists(System.IO.Path.Combine(Path, "eboot.bin"));

        string title, titleId, version;
        public string Title { get => title ?? FallbackTitle; set { title = value; Changed(); Changed(nameof(Subtitle)); } }
        public string TitleId { get => titleId; set { titleId = value; Changed(); Changed(nameof(Subtitle)); } }
        public string Version { get => version; set { version = value; Changed(); Changed(nameof(Subtitle)); } }

        string FallbackTitle => IsZar ? System.IO.Path.GetFileNameWithoutExtension(Path) : System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));

        public string Subtitle
        {
            get
            {
                if (!Exists) return "Not found: " + Path;
                var parts = new List<string> { "PS5" };
                if (!string.IsNullOrEmpty(TitleId)) parts.Add(TitleId);
                if (!string.IsNullOrEmpty(Version)) parts.Add("v" + Version);
                if (IsZar) parts.Add(".zar");
                return string.Join("  •  ", parts);
            }
        }

        ImageSource cover, backdrop;
        public ImageSource Cover { get => cover; set { cover = value; Changed(); Changed(nameof(HasCover)); } }
        public bool HasCover => cover != null;
        public ImageSource Backdrop { get => backdrop; set { backdrop = value; Changed(); } }

        ShaderState shaders;
        public ShaderState Shaders { get => shaders; set { shaders = value; Changed(); Changed(nameof(ShaderText)); } }
        public string ShaderText
        {
            get
            {
                switch (shaders)
                {
                    case ShaderState.Checking: return "Checking shaders…";
                    case ShaderState.Ready: return "Shaders precompiled";
                    case ShaderState.Outdated: return "Shader cache out of date";
                    case ShaderState.Missing: return "Shaders not precompiled";
                    case ShaderState.Recorded: return "Shaders recorded from play: warmed up at start";
                    case ShaderState.NotRecorded: return "No shaders recorded yet: they are recorded as you play";
                    default: return "";
                }
            }
        }

        bool selected;
        public bool IsSelected { get => selected; set { selected = value; Changed(); } }

        public event PropertyChangedEventHandler PropertyChanged;
        void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Title, id and version from sce_sys\param.json (a .zar's is copied out by the precompile program), and the
        /// cover art. Runs off the UI thread; the images are frozen.</summary>
        public void LoadDetails(string root)
        {
            try
            {
                var param = ReadParam(root);
                if (param != null)
                {
                    var name = TitleName(param);
                    var id = param.TryGetValue("titleId", out var t) ? Convert.ToString(t) : null;
                    var ver = param.TryGetValue("contentVersion", out var v) ? Convert.ToString(v) : null;
                    App.OnUi(() => { if (name != null) Title = name; TitleId = id; Version = ver; });
                }
            }
            catch { }
            if (IsZar) return;
            var c = LoadImage(System.IO.Path.Combine(Path, "sce_sys", "icon0.png"), 256);
            var b = LoadImage(System.IO.Path.Combine(Path, "sce_sys", "pic0.png"), 1280);
            App.OnUi(() => { Cover = c; Backdrop = b; });
        }

        Dictionary<string, object> ReadParam(string root)
        {
            var file = System.IO.Path.Combine(Path, "sce_sys", "param.json");
            if (!IsZar && File.Exists(file)) return Parse(File.ReadAllText(file));
            var tool = System.IO.Path.Combine(root, "kyty_shader_precompile.exe");
            if (!IsZar || !File.Exists(Path) || !File.Exists(tool)) return null;
            var copy = System.IO.Path.GetTempFileName();
            try
            {
                var exit = Core.Emulator.RunHidden(tool, $"--game \"{Path}\" --param \"{copy}\"", root, out _);
                return exit == 0 ? Parse(File.ReadAllText(copy)) : null;
            }
            finally { try { File.Delete(copy); } catch { } }
        }

        static Dictionary<string, object> Parse(string json) =>
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);

        static string TitleName(Dictionary<string, object> param)
        {
            if (!(param.TryGetValue("localizedParameters", out var lp) && lp is Dictionary<string, object> localized)) return null;
            var lang = localized.TryGetValue("defaultLanguage", out var d) ? Convert.ToString(d) : "en-US";
            foreach (var key in new[] { lang, "en-US", "en-GB" })
                if (key != null && localized.TryGetValue(key, out var l) && l is Dictionary<string, object> entry &&
                    entry.TryGetValue("titleName", out var n)) return Convert.ToString(n);
            return null;
        }

        static ImageSource LoadImage(string file, int width)
        {
            if (!File.Exists(file)) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = width;
                image.UriSource = new Uri(file);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }

        /// <summary>The state of the static pipeline cache for this game version, as run-windows.ps1 reads it
        /// (kyty_shader_precompile --status).</summary>
        public void CheckShaders(string root)
        {
            var tool = System.IO.Path.Combine(root, "kyty_shader_precompile.exe");
            if (string.IsNullOrEmpty(TitleId) || string.IsNullOrEmpty(Version) || !File.Exists(tool) || !Exists)
            {
                App.OnUi(() => Shaders = ShaderState.Unknown);
                return;
            }
            if (!IsDemonsSouls)
            {
                // Other games: the emulator's shader warmup records what play used (_PipelineCache\warmup-v2\<device>\
                // <title>_<version>.shaders) and compiles it at the next start; the static precompile reads Demon's Souls'
                // engine files only.
                var warm = System.IO.Path.Combine(root, "_PipelineCache", "warmup-v2");
                var recorded = Directory.Exists(warm) && Directory.GetDirectories(warm).Any(d =>
                    File.Exists(System.IO.Path.Combine(d, $"{TitleId}_{Version}.shaders")) || File.Exists(System.IO.Path.Combine(d, $"{TitleId}.shaders")));
                App.OnUi(() => Shaders = recorded ? ShaderState.Recorded : ShaderState.NotRecorded);
                return;
            }
            var seedName = $"seeds-{TitleId}_{Version}.seeds";
            var seeds = new[] { System.IO.Path.Combine(root, seedName), System.IO.Path.Combine(root, "_Build", "static-precompile", seedName) }
                .FirstOrDefault(File.Exists);
            if (seeds == null) { App.OnUi(() => Shaders = ShaderState.Missing); return; }
            App.OnUi(() => Shaders = ShaderState.Checking);
            var exit = Core.Emulator.RunHidden(tool, $"--game \"{Path}\" --seeds \"{seeds}\" --status", root, out var output);
            ShaderState state;
            if (exit != 0) state = ShaderState.Unknown;
            else if (output.Contains("static cache current"))
            {
                // A precompile that stopped before its last merge keeps its shards (run-windows.ps1 has the same).
                var dir = System.IO.Path.Combine(root, "_PipelineCache", "static");
                var shards = Directory.Exists(dir) && new DirectoryInfo(dir).GetFiles("*.shard*").Any(f => f.Length > 1 << 20);
                state = shards ? ShaderState.Outdated : ShaderState.Ready;
            }
            else if (output.Contains("static cache outdated")) state = ShaderState.Outdated;
            else state = ShaderState.Missing;
            App.OnUi(() => Shaders = state);
        }
    }

    /// <summary>launcher-library.json: the games added to the launcher, when each was last played and its own settings.</summary>
    public sealed class Library
    {
        readonly string path;
        public List<GameEntry> Games { get; } = new List<GameEntry>();
        /// <summary>The folders of the other KytyPS5 builds added (the launcher's own is not listed).</summary>
        public List<string> Emulators { get; } = new List<string>();

        public Library(string root, string current)
        {
            path = System.IO.Path.Combine(root, "launcher-library.json");
            try
            {
                if (File.Exists(path))
                {
                    var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                    if (data != null && data.TryGetValue("emulators", out var e) && e is System.Collections.ArrayList folders)
                        foreach (var f in folders) { var folder = Convert.ToString(f); if (!string.IsNullOrWhiteSpace(folder)) Emulators.Add(folder); }
                    if (data != null && data.TryGetValue("games", out var g) && g is System.Collections.ArrayList list)
                        foreach (var item in list.OfType<Dictionary<string, object>>())
                        {
                            if (!item.TryGetValue("path", out var p) || string.IsNullOrWhiteSpace(Convert.ToString(p))) continue;
                            var entry = new GameEntry(Convert.ToString(p));
                            if (item.TryGetValue("lastPlayed", out var lp) && DateTime.TryParse(Convert.ToString(lp), null,
                                System.Globalization.DateTimeStyles.RoundtripKind, out var when)) entry.LastPlayed = when;
                            if (item.TryGetValue("settings", out var gs) && gs is Dictionary<string, object> own) entry.Settings = own;
                            if (item.TryGetValue("emulator", out var em)) entry.Emulator = Convert.ToString(em) ?? "";
                            if (Find(entry.Path) == null) Games.Add(entry);
                        }
                }
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(current)) Add(current);
        }

        public GameEntry Find(string game) =>
            Games.FirstOrDefault(g => string.Equals(Normalize(g.Path), Normalize(game), StringComparison.OrdinalIgnoreCase));

        static string Normalize(string p) => (p ?? "").Trim().TrimEnd('\\', '/');

        public GameEntry Add(string game)
        {
            game = Normalize(game);
            var found = Find(game);
            if (found != null) return found;
            var entry = new GameEntry(game);
            Games.Add(entry);
            return entry;
        }

        public void Remove(GameEntry entry) => Games.Remove(entry);

        public void Save()
        {
            var games = Games.Select(g => (object)new Dictionary<string, object>
            {
                ["path"] = g.Path,
                ["lastPlayed"] = g.LastPlayed == default ? "" : g.LastPlayed.ToUniversalTime().ToString("o"),
                ["settings"] = g.Settings ?? new Dictionary<string, object>(),
                ["emulator"] = g.Emulator ?? "",
            }).ToList();
            var data = new Dictionary<string, object> { ["emulators"] = Emulators.Cast<object>().ToList(), ["games"] = games };
            try { File.WriteAllText(path, Json.Pretty(data)); } catch { }
        }
    }
}
