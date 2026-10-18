using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace KytyLauncher.Core
{
    /// <summary>
    /// Updates of the launcher itself from its GitHub releases (xlzipx/KytyPS5-Launcher), as BuildUpdates does it for the
    /// emulator builds. A running exe cannot be overwritten but can be renamed: the launcher's own files are moved aside
    /// (*.old, deleted at the next start), the new ones copied in, and the launcher restarts. Only its own files are
    /// touched, whatever else its folder holds (it may sit in a KytyPS5 build's folder).
    /// </summary>
    public static class SelfUpdate
    {
        public const string Repo = "xlzipx/KytyPS5-Launcher";

        /// <summary>The launcher's files in its release package.</summary>
        static readonly string[] Files = { "KytyPS5.exe", "KytyPS5.exe.config", "KytyPS5 Launcher - README.txt", "KytyPS5 Launcher - LICENSE.txt" };

        static string Folder => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

        public static Version Current
        {
            get { var v = typeof(SelfUpdate).Assembly.GetName().Version; return new Version(v.Major, v.Minor, v.Build); }
        }

        /// <summary>A release tag as a version ("v1.5.2", "1.5.2"); null when it is not one.</summary>
        public static Version TagVersion(string tag)
        {
            var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) : null;
        }

        /// <summary>The latest release when it is newer than this launcher, else null.</summary>
        public static BuildUpdates.Release Newer(out BuildUpdates.Release latest)
        {
            latest = BuildUpdates.Latest(Repo);
            var version = TagVersion(latest.Tag);
            return version != null && version > Current && latest.Url != null ? latest : null;
        }

        /// <summary>The files a previous update moved aside.</summary>
        public static void CleanUp()
        {
            foreach (var name in Files)
                try { var old = Path.Combine(Folder, name + ".old"); if (File.Exists(old)) File.Delete(old); } catch { }
            try { var work = Path.Combine(Folder, "_LauncherUpdate"); if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }

        /// <summary>Downloads, checks and installs the release over the launcher's own files; the caller then restarts it.
        /// Throws with the launcher as it was on any failure.</summary>
        public static void Install(BuildUpdates.Release release, Action<string> progress)
        {
            var work = Path.Combine(Folder, "_LauncherUpdate");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var package = Path.Combine(work, release.Asset);
            progress($"Downloading {release.Asset}…");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "KytyPS5-Launcher";
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
            var unpacked = Path.Combine(work, "unpacked");
            ZipFile.ExtractToDirectory(package, unpacked);
            var source = Directory.GetFiles(unpacked, "KytyPS5.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName)
                .OrderBy(d => d.Length).FirstOrDefault() ?? throw new InvalidDataException("The package holds no KytyPS5.exe.");

            progress("Installing…");
            var moved = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var name in Files)
                {
                    var from = Path.Combine(source, name);
                    if (!File.Exists(from)) continue;
                    var target = Path.Combine(Folder, name);
                    if (File.Exists(target))
                    {
                        var old = target + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(target, old);
                        moved.Add(name);
                    }
                    File.Copy(from, target, true);
                }
            }
            catch
            {
                // Back to the launcher as it was.
                foreach (var name in moved)
                    try
                    {
                        var target = Path.Combine(Folder, name);
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(target + ".old", target);
                    }
                    catch { }
                throw;
            }
            try { Directory.Delete(work, true); } catch { }
        }

        /// <summary>Starts the updated launcher (it waits until this one has closed).</summary>
        public static void Restart(string[] args)
        {
            var arguments = string.Join(" ", args.Where(a => a != "--updated").Select(a => "\"" + a + "\"").Concat(new[] { "--updated" }));
            Process.Start(new ProcessStartInfo(Path.Combine(Folder, "KytyPS5.exe"), arguments) { UseShellExecute = false, WorkingDirectory = Folder });
        }
    }
}
