using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KytyLauncher.Core
{
    /// <summary>
    /// Updates of a KytyPS5 build from its GitHub releases, as the official launcher's updater does it (src/launcher
    /// updateChecker, src/updater) but for any build: the latest release's Windows .zip, checked against the SHA-256 digest
    /// GitHub lists for it, unpacked over the build's program files. The saves, caches and settings stay; the files the
    /// package replaces are kept in _Update\backup first.
    /// The source and the installed release are kept in the build's folder (kyty-release.json).
    /// </summary>
    public static class BuildUpdates
    {
        public const string ReleaseFile = "kyty-release.json";

        public sealed class Installed
        {
            public string Repo;   // owner/name, null when unknown
            public string Tag;    // the release tag installed through this launcher, else null
            public string Git;    // kyty_emulator --help: "git = ..." (a hash or a release name)
            public string Version;
        }

        public sealed class Release
        {
            public string Tag;
            public string Page;
            public DateTime Published;
            public string Asset;
            public string Url;
            public long Size;
            public byte[] Sha256;
        }

        public sealed class Check
        {
            public Release Latest;
            public bool Available;
            public string Status;
        }

        // ---- The build ------------------------------------------------------------------------------------------------

        /// <summary>A repository from text: "owner/name", a github.com address or a releases link; null otherwise.</summary>
        public static string ParseRepo(string text)
        {
            var m = Regex.Match(text ?? "", @"^\s*(?:https?://)?(?:www\.)?(?:github\.com/)?([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?(?:/.*)?\s*$");
            if (!m.Success || m.Groups[1].Value.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
            return m.Groups[1].Value + "/" + m.Groups[2].Value;
        }

        public static Installed Read(string folder)
        {
            var result = new Installed();
            try
            {
                var file = Path.Combine(folder, ReleaseFile);
                if (File.Exists(file))
                {
                    var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                    if (values.TryGetValue("repo", out var r)) result.Repo = ParseRepo(Convert.ToString(r));
                    if (values.TryGetValue("tag", out var t)) result.Tag = Convert.ToString(t);
                }
            }
            catch { }
            var exe = Path.Combine(folder, "kyty_emulator.exe");
            if (File.Exists(exe))
            {
                Emulator.RunHidden(exe, "--help", folder, out var output);
                var m = Regex.Match(output ?? "", @"ver\s*=\s*([^,\s]+),\s*git\s*=\s*([^,\s]+)");
                if (m.Success) { result.Version = m.Groups[1].Value; result.Git = m.Groups[2].Value; }
            }
            if (result.Repo == null) result.Repo = Guess(folder, result.Git);
            return result;
        }

        /// <summary>The repository of a known build: Jetsku's u59 releases ("u59-windows-..."), chenxiao07's Demon's Souls
        /// build (run-windows.ps1, "vYYYYMMDD"), the official KytyPS5 (its kyty_updater.exe), else a github.com/…/KytyPS5
        /// link in the build's readme.</summary>
        static string Guess(string folder, string git)
        {
            git = git ?? "";
            if (git.StartsWith("u59-", StringComparison.OrdinalIgnoreCase)) return "Jetsku/KytyPS5";
            if (File.Exists(Path.Combine(folder, "run-windows.ps1")) && Regex.IsMatch(git, @"^v\d{8}")) return "chenxiao07/KytyPS5";
            if (File.Exists(Path.Combine(folder, "kyty_updater.exe")) && Regex.IsMatch(git, "^[0-9a-f]{7,40}$")) return "KytyPS5/KytyPS5";
            try
            {
                foreach (var readme in Directory.GetFiles(folder, "*README*.md"))
                {
                    var m = Regex.Match(File.ReadAllText(readme), @"github\.com/([A-Za-z0-9_.-]+/KytyPS5)\b", RegexOptions.IgnoreCase);
                    if (m.Success) return ParseRepo(m.Groups[1].Value);
                }
            }
            catch { }
            return null;
        }

        public static void SaveSource(string folder, string repo, string tag = null)
        {
            var values = new Dictionary<string, object>();
            try
            {
                var file = Path.Combine(folder, ReleaseFile);
                if (File.Exists(file)) values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file)) ?? values;
            }
            catch { }
            values["// KytyPS5 launcher"] = "where this build's updates come from (GitHub releases) and the release installed";
            values["repo"] = repo ?? "";
            if (tag != null) { values["tag"] = tag; values["installed"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"); }
            File.WriteAllText(Path.Combine(folder, ReleaseFile), Json.Pretty(values));
        }

        // ---- GitHub -----------------------------------------------------------------------------------------------------

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "KytyPS5-Launcher";
            return client;
        }

        /// <summary>The latest release: from GitHub's API, else (its 60 requests an hour without signing in used up, or the
        /// API failing) from the release pages themselves, which list the same packages and SHA-256 digests.</summary>
        public static Release Latest(string repo)
        {
            try { return LatestFromApi(repo); }
            catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound) { throw; }
            catch (Exception)
            {
                return LatestFromPages(repo);
            }
        }

        /// <summary>github.com/&lt;repo&gt;/releases/latest redirects to the latest tag; its expanded_assets page lists each
        /// package's link followed by its digest.</summary>
        static Release LatestFromPages(string repo)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create($"https://github.com/{repo}/releases/latest");
            request.AllowAutoRedirect = false;
            request.UserAgent = "KytyPS5-Launcher";
            string location;
            using (var response = (HttpWebResponse)request.GetResponse()) location = response.Headers["Location"] ?? "";
            var tag = Regex.Match(location, @"/releases/tag/([^/?#]+)").Groups[1].Value;
            if (tag.Length == 0) throw new WebException($"github.com/{repo} has no releases");
            tag = Uri.UnescapeDataString(tag);
            string html;
            using (var client = Client()) html = client.DownloadString($"https://github.com/{repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}");
            var release = new Release { Tag = tag, Page = $"https://github.com/{repo}/releases/tag/{Uri.EscapeDataString(tag)}" };
            var links = Regex.Matches(html, @"href=""(/[^""]+/releases/download/[^""]+)""").Cast<Match>().ToList();
            var packages = new List<(string Name, string Url, byte[] Sha)>();
            for (var i = 0; i < links.Count; i++)
            {
                var url = "https://github.com" + System.Net.WebUtility.HtmlDecode(links[i].Groups[1].Value);
                var name = Uri.UnescapeDataString(url.Substring(url.LastIndexOf('/') + 1));
                var end = i + 1 < links.Count ? links[i + 1].Index : html.Length;
                var digest = Regex.Match(html.Substring(links[i].Index, end - links[i].Index), "sha256:([0-9a-f]{64})");
                packages.Add((name, url, digest.Success ? Hex(digest.Groups[1].Value) : null));
            }
            var chosen = packages.Where(p => p.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(p.Name, "linux|macos|darwin|osx|android", RegexOptions.IgnoreCase))
                .OrderByDescending(p => Regex.IsMatch(p.Name, "win", RegexOptions.IgnoreCase)).ToList();
            if (chosen.Count > 0)
            {
                release.Asset = chosen[0].Name;
                release.Url = chosen[0].Url;
                release.Sha256 = chosen[0].Sha;
            }
            return release;
        }

        static byte[] Hex(string hex) => Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();

        static Release LatestFromApi(string repo)
        {
            string json;
            using (var client = Client())
            {
                client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = client.DownloadString($"https://api.github.com/repos/{repo}/releases/latest");
            }
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
            var release = new Release
            {
                Tag = Convert.ToString(root["tag_name"]),
                Page = Convert.ToString(root["html_url"]),
                Published = DateTime.TryParse(Convert.ToString(root["published_at"]), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var p) ? p : DateTime.MinValue,
            };
            // The Windows package: a .zip, named for Windows, or at least not for another system.
            var assets = ((System.Collections.ArrayList)root["assets"]).Cast<Dictionary<string, object>>()
                .Select(a => (Name: Convert.ToString(a["name"]), Asset: a))
                .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(a.Name, "linux|macos|darwin|osx|android", RegexOptions.IgnoreCase))
                .OrderByDescending(a => Regex.IsMatch(a.Name, "win", RegexOptions.IgnoreCase))
                .ToList();
            if (assets.Count > 0)
            {
                var a = assets[0].Asset;
                release.Asset = assets[0].Name;
                release.Url = Convert.ToString(a["browser_download_url"]);
                release.Size = Convert.ToInt64(a["size"]);
                var digest = a.TryGetValue("digest", out var d) ? Convert.ToString(d) : "";
                var m = Regex.Match(digest ?? "", "^sha256:([0-9a-fA-F]{64})$");
                if (m.Success) release.Sha256 = Enumerable.Range(0, 32).Select(i => Convert.ToByte(m.Groups[1].Value.Substring(i * 2, 2), 16)).ToArray();
            }
            return release;
        }

        /// <summary>Whether the latest release is newer than the build: the tag this launcher installed, else the build's own
        /// git field (a release name, or a commit hash that the official tags end with), else the exe's date.</summary>
        public static Check Compare(string folder, Installed installed, Release latest)
        {
            var check = new Check { Latest = latest };
            var git = installed.Git ?? "";
            bool same;
            if (!string.IsNullOrEmpty(installed.Tag)) same = installed.Tag == latest.Tag;
            else if (git.Length > 0 && (latest.Tag == git || latest.Tag.EndsWith("-" + git, StringComparison.OrdinalIgnoreCase) || latest.Tag.IndexOf(git, StringComparison.OrdinalIgnoreCase) >= 0)) same = true;
            else if (latest.Published == DateTime.MinValue)
            {
                // (Read from the release pages, without its date: nothing to compare the build with.)
                check.Status = $"Latest release: {latest.Tag} (this build's version is not known)";
                return check;
            }
            else same = File.GetLastWriteTimeUtc(Path.Combine(folder, "kyty_emulator.exe")) >= latest.Published;
            check.Available = !same && latest.Url != null;
            check.Status = same ? $"Up to date ({latest.Tag})"
                : latest.Url == null ? $"Release {latest.Tag} has no Windows package"
                : $"Update available: {latest.Tag}";
            return check;
        }

        // ---- Install ----------------------------------------------------------------------------------------------------

        /// <summary>The build's own data, never replaced by a package.</summary>
        static readonly string[] KeepFolders = { "_SaveData", "_PipelineCache", "_TempData", "_DownloadData", "_Textures", "_Trophies", "_DevLog", "_Update", "logs" };
        static readonly string[] KeepFiles = { "launcher-settings.json", "launcher-library.json", "game-path.txt", "no-precompile-prompt.txt", ReleaseFile, "compat-cache.json" };

        static bool Kept(string relative)
        {
            var parts = relative.Split('\\', '/');
            if (parts.Length > 1 && KeepFolders.Any(f => f.Equals(parts[0], StringComparison.OrdinalIgnoreCase))) return true;
            var name = parts[parts.Length - 1];
            return KeepFiles.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                   Regex.IsMatch(name, @"^zipeek-preset.*\.json$", RegexOptions.IgnoreCase);
        }

        /// <summary>Whether a program from the build's folder runs (the emulator or its own launcher).</summary>
        public static bool Running(string folder)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var file = p.MainModule?.FileName;
                    if (file != null && file.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(file, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }

        /// <summary>Downloads, checks and installs the release over the build. Throws with a message on any failure, leaving
        /// the build as it was (files already replaced are restored from the backup).</summary>
        public static void Install(string folder, string repo, Release release, Action<string> progress)
        {
            if (release.Url == null) throw new InvalidOperationException($"Release {release.Tag} has no Windows package.");
            if (Running(folder)) throw new InvalidOperationException("A program of this build is running: close the game (and its launcher) first.");
            var work = Path.Combine(folder, "_Update");
            var package = Path.Combine(work, release.Asset);
            var unpacked = Path.Combine(work, "unpacked");
            Directory.CreateDirectory(work);
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);

            progress($"Downloading {release.Asset} ({release.Size / 1048576.0:0.#} MB)…");
            using (var client = Client())
            {
                client.DownloadProgressChanged += (s, e) => progress($"Downloading {release.Asset}: {e.ProgressPercentage}%");
                client.DownloadFileTaskAsync(new Uri(release.Url), package).GetAwaiter().GetResult();
            }
            if (release.Size > 0 && new FileInfo(package).Length != release.Size) throw new InvalidDataException("The download is not the size GitHub lists for it.");
            if (release.Sha256 != null)
            {
                progress("Checking the SHA-256 digest…");
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(package))
                    if (!sha.ComputeHash(stream).SequenceEqual(release.Sha256)) throw new InvalidDataException("The download does not match the SHA-256 digest GitHub lists for it.");
            }

            progress("Unpacking…");
            ZipFile.ExtractToDirectory(package, unpacked);
            // The package's own root: the folder with kyty_emulator.exe (a zip may wrap it in a folder).
            var source = Directory.GetFiles(unpacked, "kyty_emulator.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName)
                .OrderBy(d => d.Length).FirstOrDefault() ?? throw new InvalidDataException("The package holds no kyty_emulator.exe.");

            var backup = Path.Combine(work, "backup");
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            var replaced = new List<string>();
            try
            {
                var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
                var i = 0;
                foreach (var file in files)
                {
                    var relative = file.Substring(source.Length + 1);
                    if (++i % 20 == 0) progress($"Installing {release.Tag}: {i * 100 / files.Length}%");
                    if (Kept(relative)) continue;
                    var target = Path.Combine(folder, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target))
                    {
                        var keep = Path.Combine(backup, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(keep));
                        File.Copy(target, keep, true);
                        replaced.Add(relative);
                    }
                    File.Copy(file, target, true);
                }
            }
            catch
            {
                // Back to the build as it was.
                foreach (var relative in replaced)
                    try { File.Copy(Path.Combine(backup, relative), Path.Combine(folder, relative), true); } catch { }
                throw;
            }
            SaveSource(folder, repo, release.Tag);
            try { Directory.Delete(unpacked, true); File.Delete(package); } catch { }
            progress($"Installed {release.Tag}");
        }
    }
}
