using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace KytyLauncher
{
    public partial class App : Application
    {
        public static void OnUi(Action action)
        {
            var d = Current?.Dispatcher;
            if (d == null) return;
            if (d.CheckAccess()) action(); else d.BeginInvoke(action);
        }

        static string RootFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KytyPS5 Launcher", "root.txt");

        static bool IsRoot(string dir) => !string.IsNullOrWhiteSpace(dir) &&
            (File.Exists(Path.Combine(dir, "kyty_emulator.exe")) || File.Exists(Path.Combine(dir, "run-windows.ps1")));

        /// <summary>The launcher's folder, where it keeps its library and settings: --root, else this program's folder when
        /// it holds a KytyPS5 build (the launcher beside kyty_emulator.exe, its home build), else the build folder an older
        /// launcher remembered, else this program's own folder (the launcher on its own: the builds are added, or the
        /// official KytyPS5 downloaded, from the launcher).</summary>
        static string FindRoot(string[] args)
        {
            var i = Array.FindIndex(args, a => a.Equals("--root", StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < args.Length && Directory.Exists(args[i + 1])) return Path.GetFullPath(args[i + 1]).TrimEnd('\\');
            var own = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (IsRoot(own)) return own;
            try { var saved = File.ReadAllText(RootFile).Trim(); if (IsRoot(saved)) return saved; } catch { }
            return own;
        }

        static System.Threading.Mutex single;

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);

        /// <summary>An error the launcher did not expect: written to launcher-error.log beside it and shown, and the launcher
        /// keeps running where it can.</summary>
        static void Report(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher-error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}\r\n\r\n");
            }
            catch { }
            try
            {
                MessageBox.Show("Something went wrong in the launcher:\n" + ex.Message + "\n\nThe details are in launcher-error.log beside KytyPS5.exe.",
                    "KytyPS5 Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, a) => { Report(a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => { if (a.ExceptionObject is Exception ex) Report(ex); };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, a) => a.SetObserved();
            // One launcher at a time (two would overwrite each other's settings): a second start brings the first to front.
            // (Not for the development aids, which run beside it.)
            if (!e.Args.Contains("--screenshot"))
            {
                single = new System.Threading.Mutex(true, @"Local\KytyPS5Launcher", out var first);
                if (!first)
                {
                    var me = System.Diagnostics.Process.GetCurrentProcess();
                    foreach (var other in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
                        if (other.Id != me.Id && other.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE
                            SetForegroundWindow(other.MainWindowHandle);
                        }
                    Shutdown();
                    return;
                }
            }
            base.OnStartup(e);
            var root = FindRoot(e.Args);
            if (root == null) { Shutdown(); return; }
            var window = new MainWindow(root);
            MainWindow = window;
            window.Show();
            var page = Array.FindIndex(e.Args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
            if (page >= 0 && page + 1 < e.Args.Length) window.ShowPage(e.Args[page + 1]);
            if (e.Args.Contains("--screenshot"))
            {
                // Development aid: renders the window to a PNG next to the program and exits.
                window.ContentRendered += async (s, a) =>
                {
                    await System.Threading.Tasks.Task.Delay(2500);
                    // --scroll <px>: the page and the Game Settings panel scrolled down first.
                    var scroll = Array.FindIndex(e.Args, x => x.Equals("--scroll", StringComparison.OrdinalIgnoreCase));
                    if (scroll >= 0 && scroll + 1 < e.Args.Length && double.TryParse(e.Args[scroll + 1], out var px))
                    {
                        window.ScrollForScreenshot(px);
                        await System.Threading.Tasks.Task.Delay(400);
                    }
                    // --download: the official KytyPS5 downloaded as the button does it.
                    if (e.Args.Contains("--download")) await window.DownloadOfficial();
                    // --focus-nav: the Logs item focused (it shows the hover beam), to see it beside the selected item.
                    if (e.Args.Contains("--focus-nav")) { window.FocusNavForScreenshot(); await System.Threading.Tasks.Task.Delay(400); }
                    window.SaveScreenshot(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshot.png"));
                    Shutdown();
                };
            }
        }
    }
}
