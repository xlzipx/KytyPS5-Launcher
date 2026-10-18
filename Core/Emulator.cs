using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KytyLauncher.Core
{
    /// <summary>
    /// The commands launcher.ps1 starts: run-windows.ps1 to play, precompile-windows.ps1 to precompile or export the
    /// shader hints, each in a console that keeps its live log. The option lists follow the KytyPS5 launcher
    /// (src/launcher, by the KytyPS5 developers), as launcher.ps1 does.
    /// </summary>
    public sealed class Emulator
    {
        public static readonly string[] Languages =
        {
            "Japanese", "English (United States)", "French (France)", "Spanish (Spain)", "German", "Italian", "Dutch",
            "Portuguese (Portugal)", "Russian", "Korean", "Chinese (Traditional)", "Chinese (Simplified)", "Finnish", "Swedish",
            "Danish", "Norwegian", "Polish", "Portuguese (Brazil)", "English (United Kingdom)", "Turkish", "Spanish (Latin America)",
            "Arabic", "French (Canada)", "Czech", "Hungarian", "Greek", "Romanian", "Thai", "Vietnamese", "Indonesian",
        };
        public static readonly string[] Resolutions =
        {
            "1280x720", "1920x1080", "2560x1440", "3840x2160", "2560x1080", "3440x1440", "3840x1600", "5120x2160", "3840x1080", "5120x1440",
        };
        // The height Demon's Souls renders its 3D at (run-windows.ps1 -RenderHeight).
        public static readonly string[] RenderHeights = { "1440 (PS5)", "1296", "1152", "1080", "900" };
        public static readonly string[] Ratios = { "16:9 (PS5)", "21:9", "32:9" };
        public static readonly string[] Qualities = { "Standard (PS5)", "Low", "Lowest" };
        public static readonly string[] FrameGens = { "Off", "2x", "3x", "4x", "5x", "6x" };
        public static readonly string[] Overlays = { "Off", "Frame rate", "Detailed" };
        public const string StreamlineSdkUrl = "https://github.com/NVIDIA-RTX/Streamline/releases";
        // The official KytyPS5 (About: GitHub, latest release).
        public const string RepositoryUrl = "https://github.com/KytyPS5/KytyPS5";

        public string Root { get; }
        public LauncherSettings Settings { get; }
        public long EfficiencyMask { get; }
        public string StreamlineDir { get; private set; }
        public bool FrameGenAvailable => StreamlineDir != null;
        public string Exe => File.Exists(Path.Combine(Root, "kyty_emulator.exe")) ? Path.Combine(Root, "kyty_emulator.exe")
            : Path.Combine(Root, "_Build", "windows", "kyty_emulator.exe");
        public bool IsInstalled => File.Exists(Path.Combine(Root, "run-windows.ps1"));
        public string NoPromptFile => Path.Combine(Root, "no-precompile-prompt.txt");

        public Emulator(string root, LauncherSettings settings)
        {
            Root = root;
            Settings = settings;
            try { EfficiencyMask = CpuSets.EfficiencyMask(); } catch { EfficiencyMask = 0; }
            StreamlineDir = ResolveStreamline();
        }

        // ---- Streamline (frame generation) --------------------------------------------------------------------------

        /// <summary>The runtime's folder in dir (the folder itself or its bin\x64) when its sl.interposer.dll is signed by
        /// NVIDIA; null otherwise.</summary>
        public static string FindStreamline(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            foreach (var folder in new[] { dir, Path.Combine(dir, "bin", "x64") })
            {
                var file = Path.Combine(folder, "sl.interposer.dll");
                if (!File.Exists(file)) continue;
                return Authenticode.IsSignedBy(file, "O=NVIDIA Corporation") ? folder : null;
            }
            return null;
        }

        string ResolveStreamline()
        {
            if (!string.IsNullOrEmpty(Settings.Streamline)) return FindStreamline(Settings.Streamline);
            var env = Environment.GetEnvironmentVariable("KYTY_STREAMLINE_DIR");
            if (!string.IsNullOrEmpty(env)) return FindStreamline(env);
            var exeDir = Path.GetDirectoryName(Exe);
            foreach (var dir in new[] { Path.Combine(exeDir, "streamline"), Path.Combine(Path.GetDirectoryName(exeDir) ?? exeDir, "deps", "streamline", "sdk") })
            {
                var found = FindStreamline(dir);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Chooses the Streamline SDK folder; false when it holds no NVIDIA-signed runtime.</summary>
        public bool SetStreamline(string dir)
        {
            var found = FindStreamline(dir);
            if (found == null) return false;
            Settings.Streamline = dir;
            StreamlineDir = found;
            return true;
        }

        // ---- Commands -----------------------------------------------------------------------------------------------

        static string Q(string s) => "\"" + s + "\"";

        public string PlayCommand(bool demonsSouls = true)
        {
            var s = Settings;
            var size = (s.Resolution ?? "2560x1440").Split('x');
            var a = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Q(Path.Combine(Root, "run-windows.ps1")), "-Prompt", "-Follow",
                "-Width", size[0], "-Height", size.Length > 1 ? size[1] : "1440", "-Language", s.Language.ToString() };
            if (s.Fullscreen) { a.Add("-Fullscreen"); if (s.Aspect) a.Add("-AspectFit"); }
            if (!s.RedZone) a.Add("-NoRedZone");
            if (s.Fps120) a.Add("-Fps120");
            // Presents wait for the display's refresh (Vulkan FIFO): no tearing, and G-SYNC/FreeSync displays keep adapting.
            if (s.VSync) a.AddRange(new[] { "-PresentMode", "Fifo" });
            if (s.Hdr) a.Add("-Hdr");
            // Demon's Souls' own render options; another game gets none of them.
            if (demonsSouls && s.Render > 0 && s.Render != 1440) a.AddRange(new[] { "-RenderHeight", s.Render.ToString() });
            if (demonsSouls && (s.Ratio == "21:9" || s.Ratio == "32:9")) a.AddRange(new[] { "-Aspect", s.Ratio });
            if (demonsSouls && (s.Quality == "low" || s.Quality == "lowest"))
                a.AddRange(new[] { "-Quality", s.Quality, "-TextureReduce", s.Quality == "lowest" ? "2" : "1" });
            if (FrameGenAvailable && s.FrameGen >= 2) a.AddRange(new[] { "-FrameGen", (s.FrameGen - 1).ToString(), "-StreamlineDir", Q(StreamlineDir) });
            // Switches of the config, one -Set argument (run-windows.ps1 splits it at the commas), as launcher.ps1 does.
            var sets = new List<string>();
            if (s.Hud >= 1) sets.Add("KYTY_FPS_HUD=" + Math.Min(s.Hud, 2));
            // Safe GPU mode: Demon's Souls' compute work one dispatch after another (no GPU-hang crashes, ~37% fewer fps).
            if (s.SafeGpu) sets.Add("KYTY_COMPUTE_CHAIN=0");
            if (sets.Count > 0) a.AddRange(new[] { "-Set", string.Join(",", sets) });
            if (!string.IsNullOrWhiteSpace(s.GamePatch) && File.Exists(s.GamePatch)) a.AddRange(new[] { "-Patch", Q(s.GamePatch) });
            if (!string.IsNullOrEmpty(s.Game)) a.AddRange(new[] { "-Game", Q(s.Game) });
            // The console stays for the live log; after a crash it waits for a key.
            return "powershell " + string.Join(" ", a) + " & if !errorlevel! neq 0 pause";
        }

        public string PrecompileCommand(string option = null)
        {
            var a = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Q(Path.Combine(Root, "precompile-windows.ps1")) };
            if (!string.IsNullOrEmpty(option)) a.Add(option);
            if (Settings.ECores && EfficiencyMask != 0) a.AddRange(new[] { "-Affinity", EfficiencyMask.ToString() });
            if (!string.IsNullOrEmpty(Settings.Game)) a.AddRange(new[] { "-Game", Q(Settings.Game) });
            return "powershell " + string.Join(" ", a) + " & pause";
        }

        public void Play(bool demonsSouls) => StartConsole("/v:on /s /c " + Q(PlayCommand(demonsSouls)));
        public void Precompile() => StartConsole("/s /c " + Q(PrecompileCommand()));
        public void ExportHints() => StartConsole("/s /c " + Q(PrecompileCommand("-ExportHints")));

        void StartConsole(string arguments)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", arguments) { WorkingDirectory = Root, UseShellExecute = true });
        }

        public string LogsFolder
        {
            get
            {
                var folder = Directory.Exists(Path.Combine(Root, "_Build")) ? Path.Combine(Root, "_Build", "run-logs") : Path.Combine(Root, "logs");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        public string ReadmeFile => new[] { Path.Combine(Root, "README.md"), Path.Combine(Root, "U59-README.md"), Path.Combine(Root, "docs", "PORTABLE-README.md") }.FirstOrDefault(File.Exists);

        /// <summary>Runs a console program without a window: its exit code and its output.</summary>
        public static int RunHidden(string file, string arguments, string workingDirectory, out string output)
        {
            output = "";
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(file, arguments)
                {
                    WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                }))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return -1; }
                    output += err.Result;
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }
    }

    /// <summary>The efficiency cores (the lowest efficiency class of a hybrid CPU) as an affinity mask, or 0
    /// (launcher.ps1's Get-EfficiencyMask).</summary>
    static class CpuSets
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemCpuSetInformation(IntPtr information, uint length, out uint returned, IntPtr process, uint flags);

        public static long EfficiencyMask()
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
            if (length == 0) return 0;
            var sets = new List<(int Cpu, int Class)>();
            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return 0;
                for (var offset = 0; offset < length; offset += Marshal.ReadInt32(buffer, offset))
                {
                    if (Marshal.ReadInt32(buffer, offset + 4) != 0 || Marshal.ReadInt16(buffer, offset + 12) != 0) continue;
                    sets.Add((Marshal.ReadByte(buffer, offset + 14), Marshal.ReadByte(buffer, offset + 18)));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            if (sets.Select(s => s.Class).Distinct().Count() < 2) return 0;
            var slowest = sets.Min(s => s.Class);
            long mask = 0;
            foreach (var s in sets.Where(s => s.Class == slowest && s.Cpu < 63)) mask |= 1L << s.Cpu;
            return mask;
        }
    }

    /// <summary>Get-AuthenticodeSignature's check: a valid signature whose signer's subject contains the given text.</summary>
    static class Authenticode
    {
        public static bool IsSignedBy(string file, string subject)
        {
            try
            {
                if (!Verify(file)) return false;
                var cert = X509Certificate.CreateFromSignedFile(file);
                return cert.Subject.IndexOf(subject, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        static bool Verify(string file)
        {
            var info = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = file };
            var pInfo = Marshal.AllocHGlobal(Marshal.SizeOf(info));
            var pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(info, pInfo, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,          // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = pInfo,
                    dwProvFlags = 0x1000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
                };
                pData = Marshal.AllocHGlobal(Marshal.SizeOf(data));
                Marshal.StructureToPtr(data, pData, false);
                return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, pData) == 0;
            }
            finally
            {
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
                Marshal.FreeHGlobal(pInfo);
            }
        }
    }
}
