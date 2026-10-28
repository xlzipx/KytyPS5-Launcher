using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KytyLauncher.Core
{
    /// <summary>
    /// A KytyPS5 build a game can run on. The Demon's Souls build (this launcher's own folder, or any with run-windows.ps1)
    /// is started through its scripts with all its options; another build (the upstream KytyPS5 with its own Qt launcher)
    /// gets kyty_emulator.exe's own command line.
    /// </summary>
    public sealed class EmulatorInstall : INotifyPropertyChanged
    {
        public EmulatorInstall(string folder, bool builtIn)
        {
            Folder = folder.TrimEnd('\\', '/');
            BuiltIn = builtIn;
        }

        public string Folder { get; }
        public bool BuiltIn { get; }
        public string Exe => Path.Combine(Folder, "kyty_emulator.exe");
        public bool Exists => File.Exists(Exe);
        /// <summary>A build with run-windows.ps1: the Demon's Souls fork's scripts and options.</summary>
        public bool IsScripted => File.Exists(Path.Combine(Folder, "run-windows.ps1"));
        /// <summary>A preset beside the emulator (u59-preset.json of the u59 builds): the environment its own launcher applies.</summary>
        public string PresetFile => !Directory.Exists(Folder) ? null
            : File.Exists(Path.Combine(Folder, "u59-preset.json")) ? Path.Combine(Folder, "u59-preset.json")
            : Directory.GetFiles(Folder, "*-preset.json").FirstOrDefault(f => !f.EndsWith("zipeek-preset.json", StringComparison.OrdinalIgnoreCase));

        /// <summary>KytyPS5 zipeek's own switches (zipeek-preset.json), applied over the build's preset.</summary>
        public string ZipeekPresetFile => File.Exists(Path.Combine(Folder, "zipeek-preset.json")) ? Path.Combine(Folder, "zipeek-preset.json") : null;

        /// <summary>The shader journal a preset build records play into (KYTY_SHADER_PRECOMPILE): its next launch translates
        /// the recorded shaders before the game asks for them. Null where the build keeps none.</summary>
        public string ShaderJournal(string titleId)
        {
            if (string.IsNullOrEmpty(titleId) || PresetFile == null) return null;
            if (Preset().TryGetValue("KYTY_SHADER_PRECOMPILE", out var on) && on == "0") return null;
            return Path.Combine(Folder, "_PipelineCache", titleId + ".shaders.journal");
        }

        /// <summary>KytyPS5 zipeek: a game's own switches beside zipeek-preset.json (zipeek-preset.&lt;title&gt;.json, e.g.
        /// PPSA01341 for Demon's Souls), the title read from the game folder's name; null without one.</summary>
        public string GamePresetFile(string game)
        {
            if (ZipeekPresetFile == null || string.IsNullOrEmpty(game)) return null;
            var m = Regex.Match(game, @"(PPSA|CUSA)\d{5}");
            if (!m.Success) return null;
            var file = Path.Combine(Folder, $"zipeek-preset.{m.Value}.json");
            return File.Exists(file) ? file : null;
        }

        /// <summary>The environment: the build's preset, zipeek-preset.json over it, and the game's own preset over both.</summary>
        Dictionary<string, string> Preset(string game = null)
        {
            var result = new Dictionary<string, string>();
            foreach (var file in new[] { PresetFile, ZipeekPresetFile, GamePresetFile(game) })
            {
                if (file == null) continue;
                try
                {
                    var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                    foreach (var kv in values)
                        if (!kv.Key.StartsWith("//")) result[kv.Key] = Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                }
                catch { }
            }
            return result;
        }

        static bool AmdCpu()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    return string.Equals(key?.GetValue("VendorIdentifier") as string, "AuthenticAMD", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        string version;
        /// <summary>kyty_emulator --help's first line: "ver = 0.3.0, git = c217380".</summary>
        public string Version { get => version; private set { version = value; Changed(); Changed(nameof(Name)); Changed(nameof(Details)); } }

        public string Name
        {
            get
            {
                if (BuiltIn && IsScripted) return "KytyPS5 – Demon's Souls build";
                var folder = Path.GetFileName(Folder);
                // A run folder inside a project (KytyPS5 zipeek\_Run) is named after the project.
                if (folder.StartsWith("_")) folder = Path.GetFileName(Path.GetDirectoryName(Folder));
                return string.IsNullOrEmpty(version) ? folder : $"{folder} ({version})";
            }
        }

        public string Details => (IsScripted ? "Scripted (run-windows.ps1): every option"
            : PresetFile != null ? "kyty_emulator.exe + " + Path.GetFileName(PresetFile) + (ZipeekPresetFile != null ? " + zipeek-preset.json" : "")
            : "kyty_emulator.exe command line") + "  •  " + Folder;

        // ---- What the build takes ---------------------------------------------------------------------------------
        // The builds differ: the upstream KytyPS5 0.3.0, Jetsku's u59 (and KytyPS5 zipeek) and the Demon's Souls fork each
        // take other command-line options and environment switches, and an option a build does not know stops it. So an
        // option is given (and its control enabled) only where the build's --help lists it or its exe holds the switch.

        /// <summary>Environment switches looked for in the exe.</summary>
        static readonly string[] KnownSwitches =
        {
            "KYTY_DISPATCH_OVERLAP", // u59: overlapping compute dispatches (0 = one after another: Safe GPU mode)
            "KYTY_COMPUTE_CHAIN",    // the Demon's Souls fork: the same (run-windows.ps1 -Set KYTY_COMPUTE_CHAIN=0)
            "KYTY_EBOOT_PRECOMPILE", // KytyPS5 zipeek: compute shaders from the executable
        };

        HashSet<string> options = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>The options whose --help line shows a value ("--trophy-notifications <true|false>"); the others are
        /// switches ("--perf-overlay"). Builds change one into the other between releases.</summary>
        HashSet<string> valued = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Whether the build's option takes a value.</summary>
        public bool TakesValue(string option) => valued.Contains(option);
        HashSet<string> switches = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Whether --help and the exe were read (until then nothing is offered as missing).</summary>
        public bool Probed { get; private set; }

        /// <summary>Whether the build takes a command-line option ("--perf-overlay"). A Demon's Souls build is started through
        /// run-windows.ps1, which passes on only its own parameters: those it maps are listed here.</summary>
        public bool Supports(string option)
        {
            if (IsScripted) return option == "--game-patch" || option == "--fullscreen" || option == "--hdr" || option == "--redzone" ||
                                   option == "--present-mode" || option == "--vblank-frequency" || option == "--console-language";
            return options.Contains(option);
        }

        /// <summary>Whether the build's exe reads an environment switch (KnownSwitches).</summary>
        public bool HasSwitch(string name) => switches.Contains(name);

        /// <summary>Safe GPU mode: compute work one dispatch after another (the fork's KYTY_COMPUTE_CHAIN=0, u59's
        /// KYTY_DISPATCH_OVERLAP=0); null where the build has neither.</summary>
        public string SafeGpuSwitch => HasSwitch("KYTY_COMPUTE_CHAIN") ? "KYTY_COMPUTE_CHAIN" : HasSwitch("KYTY_DISPATCH_OVERLAP") ? "KYTY_DISPATCH_OVERLAP" : null;

        /// <summary>An option as the build's --help describes it.</summary>
        public sealed class BuildOption
        {
            public string Name;         // "--perf-overlay"
            public string Placeholder;  // "<true|false>", "<path>", or null for a switch
            public string Description;
            public string[] Choices;    // from "<a|b|c>" (not ranges); null otherwise
            public bool IsSwitch => Placeholder == null;
            public bool IsBool => Choices != null && Choices.Length == 2 && Choices.Contains("true") && Choices.Contains("false");
            public string Label => Name.Substring(2);
        }

        /// <summary>The options the launcher sets itself (its own controls, or the game it starts): not offered again.</summary>
        static readonly HashSet<string> OwnOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "--game", "--game-patch", "--addcont-dir", "--screen-width", "--screen-height", "--user-name", "--user-id",
            "--controller-color", "--controller-volume", "--controller-vibration", "--keyboard-player", "--present-mode", "--gpu",
            "--fullscreen", "--hdr", "--hide-cursor", "--amd-cpu", "--no-amd-cpu", "--vblank-frequency", "--console-language",
            "--vulkan-validation", "--shader-validation", "--tessellation", "--readback-linear-images", "--sync-raw-image-buffers",
            "--support-zmask", "--trophy-notifications", "--no-trophy-notifications", "--trophy-duration", "--perf-overlay",
            "--skip-notice-screen", "--redzone", "--network-connected", "--rd", "--help", "--keymap",
        };

        List<BuildOption> allOptions = new List<BuildOption>();

        /// <summary>The build's options that have no control of their own in the launcher (new ones appear here as builds
        /// add them): shown under "More options of this build" in the Game Settings.</summary>
        public IReadOnlyList<BuildOption> ExtraOptions => IsScripted ? new List<BuildOption>() : allOptions.Where(o => !OwnOptions.Contains(o.Name)).ToList();

        /// <summary>The options of a --help text: "  --name <placeholder>   description", continued on indented lines.</summary>
        static List<BuildOption> ParseOptions(string help)
        {
            var result = new List<BuildOption>();
            BuildOption last = null;
            foreach (var raw in (help ?? "").Replace("\r", "").Split('\n'))
            {
                var m = Regex.Match(raw, @"^\s+(--[a-z0-9-]+)(?:[ \t]+(<[^>]*>(?:\[[^\]]*\])?))?(?:\s{2,}(.*))?$");
                if (m.Success)
                {
                    var ph = m.Groups[2].Success ? m.Groups[2].Value : null;
                    string[] choices = null;
                    var inner = ph == null ? null : Regex.Match(ph, "^<([^>]*)>$");
                    if (inner != null && inner.Success && inner.Groups[1].Value.Contains("|"))
                        choices = inner.Groups[1].Value.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
                    last = new BuildOption { Name = m.Groups[1].Value, Placeholder = ph, Description = m.Groups[3].Value.Trim(), Choices = choices };
                    if (!result.Any(o => o.Name == last.Name)) result.Add(last);
                    continue;
                }
                // A description continued on the next line (indented, no option of its own).
                if (last != null && Regex.IsMatch(raw, @"^\s{20,}\S")) last.Description = (last.Description + " " + raw.Trim()).Trim();
                else last = null;
            }
            return result;
        }

        public void LoadVersion()
        {
            if (!Exists) return;
            Emulator.RunHidden(Exe, "--help", Folder, out var output);
            var m = Regex.Match(output ?? "", @"ver\s*=\s*([^,\s]+),\s*git\s*=\s*([^,\s]+)");
            var text = m.Success ? $"{m.Groups[1].Value}, {m.Groups[2].Value}" : null;
            var lines = Regex.Matches(output ?? "", @"(?m)^\s+(--[a-z0-9-]+)([ \t]+<)?").Cast<Match>().ToList();
            var found = new HashSet<string>(lines.Select(x => x.Groups[1].Value), StringComparer.Ordinal);
            var takesValue = new HashSet<string>(lines.Where(x => x.Groups[2].Success).Select(x => x.Groups[1].Value), StringComparer.Ordinal);
            var parsed = ParseOptions(output);
            var held = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var bytes = File.ReadAllBytes(Exe);
                foreach (var name in KnownSwitches)
                    if (IndexOf(bytes, System.Text.Encoding.ASCII.GetBytes(name)) >= 0) held.Add(name);
            }
            catch { }
            App.OnUi(() =>
            {
                options = found;
                valued = takesValue;
                allOptions = parsed;
                switches = held;
                Probed = true;
                if (text != null) Version = text;
                Changed(nameof(Probed));
            });
        }

        static int IndexOf(byte[] data, byte[] needle)
        {
            for (var i = 0; i <= data.Length - needle.Length; i++)
            {
                if (data[i] != needle[0]) continue;
                var j = 1;
                while (j < needle.Length && data[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        public static readonly bool IsAmdCpu = AmdCpu();

        /// <summary>The command line of a build without the scripts: the options of the upstream launcher (src/launcher,
        /// KytyPS5 0.3.0), each only where this build's --help lists it.</summary>
        public string DirectArguments(LauncherSettings s, string game)
        {
            var size = (s.Resolution ?? "2560x1440").Split('x');
            var a = new List<string> { "--game", Q(game), "--screen-width", size[0], "--screen-height", size.Length > 1 ? size[1] : "1440",
                "--console-language", s.Language.ToString() };
            void flag(string option, bool on) { if (on && Supports(option)) a.Add(option); }
            void value(string option, string v) { if (!string.IsNullOrEmpty(v) && Supports(option)) a.AddRange(new[] { option, v }); }
            // An on/off option as this build spells it: "--x true|false" where its --help shows a value, else the switch
            // "--x" when on (and, where the build has it, "--no-x" when off and off is not its default).
            void toggle(string option, bool on, bool defaultOn = false)
            {
                if (!Supports(option))
                {
                    var negative = "--no-" + option.Substring(2);
                    if (!on && defaultOn && Supports(negative)) a.Add(negative);
                    return;
                }
                if (TakesValue(option)) { if (on != defaultOn) a.AddRange(new[] { option, on ? "true" : "false" }); }
                else if (on) a.Add(option);
            }

            // Game Settings.
            flag("--fullscreen", s.Fullscreen);
            flag("--hdr", s.Hdr);
            flag("--redzone", s.RedZone);
            // VSync: presents wait for the display (FIFO); off: the build's default, Mailbox.
            if (s.VSync) value("--present-mode", "Fifo");
            if (s.Fps120) value("--vblank-frequency", "120");
            // The game's instructions that differ on an AMD CPU ("AMD CPU patch" of the upstream launcher); u59 builds
            // turn it on by themselves on AMD and take --no-amd-cpu to turn it off.
            if (s.AmdCpu) flag("--amd-cpu", true);
            else if (IsAmdCpu) flag("--no-amd-cpu", true);
            flag("--tessellation", s.Tessellation);
            toggle("--readback-linear-images", s.ReadbackLinear);
            toggle("--sync-raw-image-buffers", s.SyncRawImages);
            toggle("--support-zmask", s.ZMask);
            toggle("--perf-overlay", s.Hud >= 1);
            toggle("--skip-notice-screen", s.SkipLogos);
            if (!string.IsNullOrWhiteSpace(s.AddCont) && Directory.Exists(s.AddCont)) value("--addcont-dir", Q(s.AddCont));
            if (!string.IsNullOrWhiteSpace(s.GamePatch) && File.Exists(s.GamePatch)) value("--game-patch", Q(s.GamePatch));

            // Settings (the launcher's, for every game).
            var user = Regex.Replace(s.UserName ?? "", "[\"%^&|<>\\r\\n]", "").Trim();
            if (user.Length > 0) value("--user-name", Q(user));
            if (s.UserId > 0) value("--user-id", s.UserId.ToString());
            if (s.Gpu >= 0) value("--gpu", s.Gpu.ToString());
            if (Regex.IsMatch(s.Lightbar ?? "", "^#[0-9A-Fa-f]{6}$")) value("--controller-color", s.Lightbar);
            if (s.Vibration != 100) value("--controller-vibration", s.Vibration.ToString());
            if (s.SpeakerVolume != 50) value("--controller-volume", s.SpeakerVolume.ToString());
            if (s.KeyboardPlayer > 0) value("--keyboard-player", s.KeyboardPlayer.ToString());
            toggle("--network-connected", s.Network);
            toggle("--trophy-notifications", s.Trophies, defaultOn: true);
            if (s.Trophies && s.TrophyDuration != 6) value("--trophy-duration", s.TrophyDuration.ToString());
            flag("--hide-cursor", s.HideCursor);
            toggle("--vulkan-validation", s.VulkanValidation);
            toggle("--shader-validation", s.ShaderValidation);
            flag("--rd", s.RenderDoc);

            // The game's "More options of this build": each one this build still has, as it spells it now.
            foreach (var kv in s.Extra)
            {
                var option = allOptions.FirstOrDefault(o => o.Name == kv.Key);
                if (option == null || OwnOptions.Contains(option.Name)) continue;
                var v = Convert.ToString(kv.Value) ?? "";
                if (option.IsSwitch) { if (v == "true") a.Add(option.Name); continue; }
                v = Regex.Replace(v, "[\"%^&|<>\r\n]", "").Trim();
                if (v.Length == 0) continue;
                if (option.Choices != null && !option.Choices.Contains(v)) continue;
                a.AddRange(new[] { option.Name, v.Contains(" ") ? Q(v) : v });
            }
            return string.Join(" ", a);
        }

        /// <summary>The environment switches of the Game Settings this build reads: Safe GPU mode, and KytyPS5 zipeek's
        /// eboot precompile for this game (over zipeek-preset.json).</summary>
        public Dictionary<string, string> GameSwitches(LauncherSettings s)
        {
            var result = new Dictionary<string, string>();
            if (s.SafeGpu && SafeGpuSwitch != null) result[SafeGpuSwitch] = "0";
            // (Off leaves the presets' values: zipeek-preset.PPSA01341.json reads Demon's Souls' strides at run time.)
            if (s.EbootPrecompile && HasSwitch("KYTY_EBOOT_PRECOMPILE"))
            {
                result["KYTY_EBOOT_PRECOMPILE"] = "1";
                result["KYTY_DYNAMIC_BUFFER_STRIDES"] = "1";
                result["KYTY_EBOOT_PRECOMPILE_CHECK"] = "0";
            }
            return result;
        }

        public void PlayDirect(LauncherSettings s, string game)
        {
            // As the build's own launcher: its preset's environment, the inherited KYTY/TRACY variables cleared. The emulator
            // opens its own console window with its log (KytyPS5 zipeek's own build: a log file instead).
            var start = new ProcessStartInfo(Exe, DirectArguments(s, game)) { WorkingDirectory = Folder, UseShellExecute = false };
            // KytyPS5 zipeek: the emulator's output into logs\play-<time>.log (the Logs page reads it, and the pipeline
            // statistics of a play session can be measured afterwards), through cmd so that it keeps writing after the
            // launcher closes.
            if (ZipeekPresetFile != null)
            {
                var logs = Path.Combine(Folder, "logs");
                Directory.CreateDirectory(logs);
                var log = Path.Combine(logs, $"play-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                start.FileName = "cmd.exe";
                start.Arguments = "/s /c \"" + Q(Exe) + " " + DirectArguments(s, game) + " > " + Q(log) + " 2>&1\"";
                start.CreateNoWindow = true;
            }
            if (PresetFile != null)
            {
                foreach (var name in start.EnvironmentVariables.Keys.Cast<string>().ToList())
                    if (name.StartsWith("KYTY_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("TRACY_", StringComparison.OrdinalIgnoreCase))
                        start.EnvironmentVariables.Remove(name);
                foreach (var kv in Preset(game)) start.EnvironmentVariables[kv.Key] = kv.Value;
            }
            foreach (var kv in GameSwitches(s)) start.EnvironmentVariables[kv.Key] = kv.Value;
            Process.Start(start);
        }

        /// <summary>KytyPS5 zipeek: the shader seeds of the Demon's Souls fork converted at start-up (KYTY_FORK_SEEDS,
        /// _PipelineCache\&lt;title&gt;.fork.seeds); null where this build has none for the game.</summary>
        public string ForkSeeds(string titleId)
        {
            if (ZipeekPresetFile == null || string.IsNullOrEmpty(titleId)) return null;
            var file = Path.Combine(Folder, "_PipelineCache", titleId + ".fork.seeds");
            return File.Exists(file) ? file : null;
        }

        /// <summary>KytyPS5 zipeek: whether "Prepare shaders" has to run for the game: it has fork seeds, and this build has
        /// not prepared it since the seeds or the emulator changed (_PipelineCache\&lt;title&gt;.prepared, written by the
        /// emulator when a prepare run finished).</summary>
        public bool NeedsPrepare(string titleId)
        {
            var seeds = ForkSeeds(titleId);
            if (seeds == null) return false;
            var marker = Path.Combine(Folder, "_PipelineCache", titleId + ".prepared");
            if (!File.Exists(marker)) return true;
            var done = File.GetLastWriteTimeUtc(marker);
            return done < File.GetLastWriteTimeUtc(seeds) || done < File.GetLastWriteTimeUtc(Exe);
        }

        /// <summary>KytyPS5 zipeek: "Prepare shaders" before playing (KYTY_PIPELINE_LIST_PREPARE=1): the emulator boots the
        /// game far enough to know it, compiles every shader and pipeline of the shader journal and the fork seeds into
        /// _PipelineCache with every core, saves and exits before the game runs. Its console window shows the progress; an
        /// interrupted run resumes where the saved caches end. Exit code 0 when it finished.</summary>
        public Process PrepareDirect(LauncherSettings s, string game)
        {
            var start = new ProcessStartInfo(Exe, DirectArguments(s, game)) { WorkingDirectory = Folder, UseShellExecute = false };
            foreach (var name in start.EnvironmentVariables.Keys.Cast<string>().ToList())
                if (name.StartsWith("KYTY_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("TRACY_", StringComparison.OrdinalIgnoreCase))
                    start.EnvironmentVariables.Remove(name);
            foreach (var kv in Preset(game)) start.EnvironmentVariables[kv.Key] = kv.Value;
            foreach (var kv in GameSwitches(s)) start.EnvironmentVariables[kv.Key] = kv.Value;
            start.EnvironmentVariables["KYTY_PIPELINE_LIST_PREPARE"] = "1";
            start.EnvironmentVariables["KYTY_PIPELINE_STATS"] = "0";
            return Process.Start(start);
        }

        static string Q(string s) => "\"" + s + "\"";

        // ---- Updates (BuildUpdates) -------------------------------------------------------------------------------

        /// <summary>Whether the launcher updates this build from GitHub releases: not KytyPS5 zipeek, which takes upstream
        /// changes through its scanner.</summary>
        public bool Updatable => ZipeekPresetFile == null;

        string updateRepo, updateStatus, repoInput;
        bool updateAvailable, updateBusy;
        /// <summary>The GitHub repository (owner/name) the updates come from; null while unknown.</summary>
        public string UpdateRepo { get => updateRepo; set { updateRepo = value; Changed(); Changed(nameof(NeedsRepo)); } }
        public string UpdateStatus { get => updateStatus; set { updateStatus = value; Changed(); } }
        public bool UpdateAvailable { get => updateAvailable; set { updateAvailable = value; Changed(); } }
        public bool UpdateBusy { get => updateBusy; set { updateBusy = value; Changed(); } }
        /// <summary>The repository the user types for a build the launcher does not know.</summary>
        public string RepoInput { get => repoInput; set { repoInput = value; Changed(); } }
        public bool NeedsRepo => Updatable && string.IsNullOrEmpty(updateRepo) && updateStatus != null;
        public BuildUpdates.Release LatestRelease { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        public void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
