using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KytyLauncher.Core
{
    /// <summary>A game of the KytyPS5 compatibility list (kytyps5.github.io, data/compat-index.json).</summary>
    public sealed class CompatGame : INotifyPropertyChanged
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string TitleId { get; set; }
        public string CoverUrl { get; set; }
        public string Status { get; set; }               // in-game, main-menu, logo, doesnt-boot (or a later one)
        public string Windows { get; set; }
        public string Linux { get; set; }
        public string MacOs { get; set; }
        public int Reports { get; set; }
        public DateTime LatestTested { get; set; }
        public string Change { get; set; }                // "new", "improved", "regressed" or null (within the last week)
        public string ChangeFrom { get; set; }

        public string StatusText => Compatibility.StatusName(Status);
        public int Rank => Compatibility.Rank(Status);
        public string TestedText => LatestTested == default ? "" : "tested " + LatestTested.ToString("d. M. yyyy", CultureInfo.InvariantCulture);
        public string ReportsText => Reports == 1 ? "1 report" : Reports + " reports";
        public string ChangeText => Change == "new" ? "NEW" : Change == "improved" ? "IMPROVED" : Change == "regressed" ? "WORSE" : null;
        public string ChangeTip => Change == "new" ? "New in the list since your earlier checks"
            : ChangeFrom != null ? $"Was {Compatibility.StatusName(ChangeFrom)}" : null;
        /// <summary>The platforms with reports, as chips: (name, status).</summary>
        public List<OsStatus> Platforms => new[] { new OsStatus("Windows", Windows), new OsStatus("Linux", Linux), new OsStatus("macOS", MacOs) }
            .Where(o => !string.IsNullOrEmpty(o.Status)).ToList();
        public string Url => "https://kytyps5.github.io/game/" + Uri.EscapeDataString(Key ?? TitleId ?? "");

        bool inLibrary;
        public bool InLibrary { get => inLibrary; set { inLibrary = value; Changed(); } }

        ImageSource cover;
        bool coverRequested;
        /// <summary>The cover, downloaded the first time a row shows it (the list virtualizes its rows).</summary>
        public ImageSource Cover
        {
            get
            {
                if (!coverRequested && !string.IsNullOrEmpty(CoverUrl))
                {
                    coverRequested = true;
                    try
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.UriSource = new Uri(CoverUrl);
                        image.DecodePixelWidth = 120;
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.EndInit();
                        cover = image;
                    }
                    catch { }
                }
                return cover;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class OsStatus
    {
        public OsStatus(string name, string status) { Name = name; Status = status; }
        public string Name { get; }
        public string Status { get; }
    }

    /// <summary>
    /// The community compatibility list, refreshed live: fetched with its ETag (the site caches it for 10 minutes, so a
    /// check costs one small request when nothing changed), kept in compat-cache.json beside the launcher for offline use,
    /// and compared with the previous list so that new games and status changes of the last week stand out.
    /// </summary>
    public sealed class Compatibility
    {
        public const string IndexUrl = "https://kytyps5.github.io/data/compat-index.json";
        public const string SiteUrl = "https://kytyps5.github.io/compatibility";
        static readonly TimeSpan ChangeWindow = TimeSpan.FromDays(7);

        readonly string cacheFile;
        string etag;
        Dictionary<string, Dictionary<string, object>> changes = new Dictionary<string, Dictionary<string, object>>();

        public List<CompatGame> Games { get; private set; } = new List<CompatGame>();
        public DateTime LastChecked { get; private set; }
        public DateTime LastChanged { get; private set; }
        public string LastError { get; private set; }

        public Compatibility(string root)
        {
            cacheFile = Path.Combine(root, "compat-cache.json");
            LoadCache();
        }

        public static string StatusName(string status)
        {
            switch (status)
            {
                case "in-game": return "In-game";
                case "main-menu": return "Main menu";
                case "logo": return "Logo";
                case "doesnt-boot": return "Doesn't boot";
                case "playable": return "Playable";
                case "perfect": return "Perfect";
                case null: case "": return "—";
                default: return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(status.Replace('-', ' '));
            }
        }

        /// <summary>How far a game gets: higher is better (status changes are compared with it).</summary>
        public static int Rank(string status)
        {
            switch (status)
            {
                case "doesnt-boot": return 0;
                case "logo": return 1;
                case "main-menu": return 2;
                case "in-game": return 3;
                case "playable": return 4;
                case "perfect": return 5;
                default: return -1;
            }
        }

        static string S(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        static Dictionary<string, object> D(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        static List<Dictionary<string, object>> ParseIndex(string json)
        {
            var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
            return data != null && data.TryGetValue("games", out var g) && g is System.Collections.ArrayList list
                ? list.OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();
        }

        List<CompatGame> Build(List<Dictionary<string, object>> raw)
        {
            var now = DateTime.UtcNow;
            var result = new List<CompatGame>();
            foreach (var g in raw)
            {
                var os = D(g, "os");
                var counts = D(g, "reportCounts");
                var game = new CompatGame
                {
                    Key = S(g, "key"), Title = S(g, "title"), TitleId = S(g, "titleId"), CoverUrl = S(g, "cover"),
                    Status = S(g, "overall"), Windows = S(os, "windows"), Linux = S(os, "linux"), MacOs = S(os, "macos"),
                    Reports = counts?.Values.Sum(v => { try { return Convert.ToInt32(v); } catch { return 0; } }) ?? 0,
                };
                if (DateTime.TryParse(S(g, "latestTested"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var tested)) game.LatestTested = tested.Date;
                if (game.Key != null && changes.TryGetValue(game.Key, out var c) && DateTime.TryParse(S(c, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) && now - at < ChangeWindow)
                {
                    game.Change = S(c, "kind");
                    game.ChangeFrom = S(c, "from");
                }
                result.Add(game);
            }
            return result;
        }

        void LoadCache()
        {
            try
            {
                if (!File.Exists(cacheFile)) return;
                var cache = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(cacheFile));
                etag = S(cache, "etag");
                if (DateTime.TryParse(S(cache, "checked"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var chk)) LastChecked = chk;
                if (DateTime.TryParse(S(cache, "changed"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var chg)) LastChanged = chg;
                if (D(cache, "changes") is Dictionary<string, object> c)
                    changes = c.Where(kv => kv.Value is Dictionary<string, object>).ToDictionary(kv => kv.Key, kv => (Dictionary<string, object>)kv.Value);
                if (cache.TryGetValue("index", out var index) && index is string json) Games = Build(ParseIndex(json));
            }
            catch { }
        }

        void SaveCache(string json)
        {
            try
            {
                var cache = new Dictionary<string, object>
                {
                    ["etag"] = etag ?? "", ["checked"] = LastChecked.ToString("o"), ["changed"] = LastChanged.ToString("o"),
                    ["changes"] = changes, ["index"] = json,
                };
                File.WriteAllText(cacheFile, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(cache));
            }
            catch { }
        }

        string cachedJson;

        /// <summary>Checks the list (off the UI thread): true when it changed since the last check.</summary>
        public bool Refresh()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(IndexUrl);
                request.UserAgent = "KytyPS5-Launcher";
                request.Timeout = 20000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                if (!string.IsNullOrEmpty(etag) && Games.Count > 0) request.Headers[HttpRequestHeader.IfNoneMatch] = etag;
                string json;
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        json = reader.ReadToEnd();
                        etag = response.Headers[HttpResponseHeader.ETag];
                    }
                }
                catch (WebException e) when (e.Response is HttpWebResponse r && r.StatusCode == HttpStatusCode.NotModified)
                {
                    LastChecked = DateTime.UtcNow;
                    LastError = null;
                    SaveCache(cachedJson ?? ReadCachedIndex());
                    return false;
                }
                var raw = ParseIndex(json);
                if (raw.Count == 0) throw new InvalidDataException("the list came back empty");
                RecordChanges(raw);
                Games = Build(raw);
                LastChecked = LastChanged = DateTime.UtcNow;
                LastError = null;
                cachedJson = json;
                SaveCache(json);
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                return false;
            }
        }

        string ReadCachedIndex()
        {
            try
            {
                var cache = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(cacheFile));
                return S(cache, "index");
            }
            catch { return null; }
        }

        /// <summary>New games and status changes against the list the launcher had (none on the very first download).</summary>
        void RecordChanges(List<Dictionary<string, object>> raw)
        {
            if (Games.Count == 0) return;
            var before = Games.Where(g => g.Key != null).GroupBy(g => g.Key).ToDictionary(g => g.Key, g => g.First().Status);
            var at = DateTime.UtcNow.ToString("o");
            foreach (var g in raw)
            {
                var key = S(g, "key");
                var status = S(g, "overall");
                if (key == null) continue;
                if (!before.TryGetValue(key, out var old))
                    changes[key] = new Dictionary<string, object> { ["kind"] = "new", ["at"] = at };
                else if (old != status && Rank(status) >= 0 && Rank(old) >= 0)
                    changes[key] = new Dictionary<string, object> { ["kind"] = Rank(status) > Rank(old) ? "improved" : "regressed", ["from"] = old, ["at"] = at };
            }
            var cutoff = DateTime.UtcNow - ChangeWindow;
            foreach (var key in changes.Keys.ToList())
                if (!DateTime.TryParse(S(changes[key], "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) || when < cutoff)
                    changes.Remove(key);
        }

        static string Normalize(string title) => Regex.Replace((title ?? "").ToLowerInvariant(), @"[^a-z0-9]+", "");

        /// <summary>Marks the games of the library: the same title ID, or the same title (other regions have other IDs).</summary>
        public void MarkLibrary(IEnumerable<GameEntry> library)
        {
            var ids = new HashSet<string>(library.Select(g => g.TitleId).Where(t => !string.IsNullOrEmpty(t)), StringComparer.OrdinalIgnoreCase);
            var titles = new HashSet<string>(library.Select(g => Normalize(g.Title)).Where(t => t.Length > 0));
            foreach (var game in Games) game.InLibrary = ids.Contains(game.TitleId ?? "") || titles.Contains(Normalize(game.Title));
        }

        /// <summary>The list's entry for a library game (by ID, else by title).</summary>
        public CompatGame Find(GameEntry game)
        {
            if (game == null) return null;
            return Games.FirstOrDefault(g => string.Equals(g.TitleId, game.TitleId, StringComparison.OrdinalIgnoreCase))
                ?? Games.FirstOrDefault(g => Normalize(g.Title) == Normalize(game.Title) && Normalize(game.Title).Length > 0);
        }
    }
}
