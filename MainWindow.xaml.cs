using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KytyLauncher.Core;

namespace KytyLauncher
{
    public partial class MainWindow : Window
    {
        readonly string root;
        readonly LauncherSettings settings;
        readonly Emulator emulator;
        readonly Library library;
        readonly ObservableCollection<GameEntry> games;
        readonly ICollectionView view;
        readonly ObservableCollection<EmulatorInstall> emulators = new ObservableCollection<EmulatorInstall>();
        readonly EmulatorInstall ownBuild;
        readonly Compatibility compat;
        readonly DispatcherTimer compatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        bool compatChecking;
        bool loading = true;

        public MainWindow(string root)
        {
            this.root = root;
            InitializeComponent();
            settings = new LauncherSettings(root);
            emulator = new Emulator(root, settings);
            library = new Library(root, settings.Game);
            games = new ObservableCollection<GameEntry>(library.Games);
            view = CollectionViewSource.GetDefaultView(games);
            view.Filter = o => Matches((GameEntry)o);
            GameList.ItemsSource = view;

            // The home build: the launcher's own folder, when it holds one (the launcher on its own has none).
            ownBuild = new EmulatorInstall(root, true);
            if (ownBuild.Exists) emulators.Add(ownBuild);
            foreach (var folder in library.Emulators) emulators.Add(new EmulatorInstall(folder, false));
            EmulatorList.ItemsSource = emulators;
            EmulatorBox.ItemsSource = emulators;
            foreach (var em in emulators) Watch(em);
            if (!settings.Has("precompilePrompt")) SyncPrecompilePrompt(true);
            MigrateEbootPrecompile();

            var version = typeof(MainWindow).Assembly.GetName().Version;
            VersionText.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";
            AboutVersion.Text = $"KytyPS5 Launcher {version.Major}.{version.Minor}.{version.Build}";
            RootText.Text = "Launcher folder (library and settings): " + root;

            FillSettings();
            SortBox.SelectedIndex = 0;
            var current = library.Find(settings.Game) ?? games.FirstOrDefault();
            GameList.SelectedItem = current;
            UpdateEmpty();
            UpdateNoBuild();
            loading = false;

            foreach (var game in games) LoadGame(game);

            // The compatibility list: the cached one at once, then a check now and every 10 minutes (the site's own cache time).
            compat = new Compatibility(root);
            CompatSort.SelectedIndex = 0;
            ShowCompat();
            compatTimer.Tick += (s, e) => CheckCompat(false);
            compatTimer.Start();
            CheckCompat(false);
            if (settings.CheckUpdates) Loaded += (s, e) => { CheckAllBuilds(false); CheckLauncher(false); };
            Loaded += (s, e) => WarnIfReadOnly();

            SourceInitialized += (s, e) => RoundCorners();
            StateChanged += (s, e) => UpdateMaximized();
            PreviewKeyDown += Window_PreviewKeyDown;
            Closing += (s, e) => { ReadSettings(); StoreGameSettings(); settings.Save(); library.Save(); };
        }

        // ---- Window chrome ------------------------------------------------------------------------------------------

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        void RoundCorners()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int round = 2, dark = 1;
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE: round
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));  // DWMWA_USE_IMMERSIVE_DARK_MODE
                int border = 0x00402619;                                  // COLORREF of #192640
                DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
                RootBorder.BorderThickness = new Thickness(0);
            }
            catch { }
        }

        void UpdateMaximized()
        {
            // A maximized borderless window reaches past the screen by its resize border.
            RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
            MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }

        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        public void SaveScreenshot(string file)
        {
            var content = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen()) dc.DrawRectangle((Brush)FindResource("WindowBrush"), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            bitmap.Render(background);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }

        public void FocusNavForScreenshot() => Keyboard.Focus(NavLogs);

        public void ScrollForScreenshot(double px)
        {
            PageSettings.ScrollToVerticalOffset(px);
            GameSettingsScroll.ScrollToVerticalOffset(px);
        }

        public void ShowPage(string page)
        {
            switch ((page ?? "").ToLowerInvariant())
            {
                case "settings": NavSettings.IsChecked = true; break;
                case "grid": NavGames.IsChecked = true; ViewTiles.IsChecked = true; break;
                case "compat": case "compatibility": NavCompat.IsChecked = true; break;
                case "logs": NavLogs.IsChecked = true; break;
                case "about": NavAbout.IsChecked = true; break;
                default: NavGames.IsChecked = true; break;
            }
        }

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageGames == null) return;
            PageGames.Visibility = NavGames.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageCompat.Visibility = NavCompat.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageLogs.Visibility = NavLogs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateBackdrop();
            if (NavLogs.IsChecked == true) LoadLogs();
            if (NavCompat.IsChecked == true && compat != null)
            {
                compat.MarkLibrary(games);
                if (DateTime.UtcNow - compat.LastChecked > TimeSpan.FromMinutes(2)) CheckCompat(false);
            }
            var page = new[] { PageGames, PageCompat, PageSettings, (FrameworkElement)PageLogs, PageAbout }.First(p => p.Visibility == Visibility.Visible);
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && NavGames.IsChecked == true)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused)
            {
                SearchBox.Text = "";
                GameList.Focus();
                e.Handled = true;
            }
        }

        // ---- Library ------------------------------------------------------------------------------------------------

        GameEntry Selected => GameList.SelectedItem as GameEntry;

        void LoadGame(GameEntry game)
        {
            Task.Run(() => game.LoadDetails(root)).ContinueWith(_ => App.OnUi(() =>
            {
                // The shader state comes from the build the game runs on: a Demon's Souls build's static cache, a preset build's
                // shader journal, else nothing to show.
                var build = EmulatorFor(game);
                if (build.IsScripted) Task.Run(() => game.CheckShaders(build.Folder)).ContinueWith(__ => App.OnUi(UpdateSelectedGame));
                else
                {
                    var journal = build.ShaderJournal(game.TitleId);
                    game.Shaders = journal == null ? ShaderState.Unknown : File.Exists(journal) ? ShaderState.Recorded : ShaderState.NotRecorded;
                }
                view.Refresh();
                UpdateSelectedGame();
                // The game's title is known now: mark it in the compatibility list.
                if (compat != null) { compat.MarkLibrary(games); compatView?.Refresh(); }
            }));
        }

        // ---- Emulators ----------------------------------------------------------------------------------------------

        static bool SameFolder(string a, string b) =>
            string.Equals((a ?? "").TrimEnd('\\', '/'), (b ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        /// <summary>The build a game runs on: its choice, else Demon's Souls on this build and another game on the first
        /// other build added (this one where there is none).</summary>
        EmulatorInstall EmulatorFor(GameEntry game)
        {
            // Without any build: the (missing) home build; Play then offers to get one.
            var home = emulators.FirstOrDefault(e => e.Exists) ?? ownBuild;
            if (game == null) return home;
            if (!string.IsNullOrEmpty(game.Emulator))
            {
                var chosen = emulators.FirstOrDefault(e => SameFolder(e.Folder, game.Emulator));
                if (chosen != null) return chosen;
            }
            // Demon's Souls: a Demon's Souls build (scripts, static precompile); another game: the first other build (the home
            // build is first in the list).
            if (game.IsDemonsSouls) return emulators.FirstOrDefault(e => e.IsScripted && e.Exists) ?? home;
            return emulators.FirstOrDefault(e => !e.IsScripted && e.Exists) ?? home;
        }

        /// <summary>The Demon's Souls build (run-windows.ps1) for precompile, shader hints and its logs: the selected game's
        /// build when it is one, else the first in the list; null without one.</summary>
        Emulator ScriptedBuild()
        {
            var build = EmulatorFor(Selected);
            if (!build.IsScripted) build = emulators.FirstOrDefault(e => e.IsScripted && e.Exists);
            if (build == null) return null;
            return SameFolder(build.Folder, root) ? emulator : new Emulator(build.Folder, settings);
        }

        /// <summary>Reads what a build takes (--help, its exe) in the background; the Game Settings follow when it is known.</summary>
        void Watch(EmulatorInstall em)
        {
            em.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(EmulatorInstall.Probed) && !loading) UpdateDependent();
                if (e.PropertyName == nameof(EmulatorInstall.UpdateAvailable) || e.PropertyName == nameof(EmulatorInstall.UpdateBusy) ||
                    e.PropertyName == nameof(EmulatorInstall.UpdateStatus)) UpdateSidebarStatus();
            };
            Task.Run(() => em.LoadVersion());
        }

        void Emulator_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (loading || Selected == null || !(EmulatorBox.SelectedItem is EmulatorInstall chosen)) return;
            if (SameFolder(EmulatorFor(Selected).Folder, chosen.Folder) && string.IsNullOrEmpty(Selected.Emulator)) return;
            Selected.Emulator = chosen.Folder;
            library.Save();
            UpdateDependent();
            LoadGame(Selected);
        }

        void EmulatorAdd_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "A KytyPS5 build: the folder with kyty_emulator.exe", ShowNewFolderButton = false })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var folder = dialog.SelectedPath.TrimEnd('\\', '/');
                if (!File.Exists(Path.Combine(folder, "kyty_emulator.exe")))
                {
                    MessageBox.Show(this, "That folder has no kyty_emulator.exe.", "KytyPS5", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (emulators.Any(x => SameFolder(x.Folder, folder))) { ShowToast("That build is already in the list"); return; }
                AddBuild(folder);
                ShowToast("Added " + Path.GetFileName(folder));
            }
        }

        EmulatorInstall AddBuild(string folder)
        {
            var added = new EmulatorInstall(folder, false);
            emulators.Add(added);
            library.Emulators.Add(folder);
            library.Save();
            Watch(added);
            _ = CheckBuild(added);
            SyncPrecompilePrompt(false);
            foreach (var game in games) LoadGame(game);
            UpdateSelectedGame();
            UpdateNoBuild();
            return added;
        }

        // ---- Out of the box: no KytyPS5 build yet ---------------------------------------------------------------------

        const string OfficialRepo = "KytyPS5/KytyPS5";

        /// <summary>Where the official KytyPS5 is downloaded to: a KytyPS5 folder beside the launcher.</summary>
        string DownloadFolder => Path.Combine(root, "KytyPS5");

        /// <summary>The banner on the Games page and the button in Settings while no build is in the list.</summary>
        void UpdateNoBuild()
        {
            var none = !emulators.Any(e => e.Exists);
            NoBuildBanner.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            DownloadBuildBtn.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            NoBuildText.Text = "The launcher runs games on KytyPS5, the PS5 emulator, which comes separately. Download its latest official release " +
                               "into a KytyPS5 folder beside the launcher (about 30 MB, one click), or choose a KytyPS5 folder you already have.";
            NoBuildText.ToolTip = DownloadFolder;
            UpdateSidebarStatus();
        }

        bool downloading;

        async void DownloadBuild_Click(object sender, RoutedEventArgs e) => await DownloadOfficial();

        /// <summary>Downloads the latest official KytyPS5 into DownloadFolder and adds it (also the --download development aid).</summary>
        public async Task DownloadOfficial()
        {
            if (downloading) return;
            downloading = true;
            foreach (var b in new[] { NoBuildDownload, DownloadBuildBtn }) b.IsEnabled = false;
            var folder = DownloadFolder;
            void say(string text) => App.OnUi(() => NoBuildStatus.Text = text);
            NoBuildStatus.Visibility = Visibility.Visible;
            say("Looking up the latest KytyPS5 release…");
            try
            {
                await Task.Run(() =>
                {
                    var release = BuildUpdates.Latest(OfficialRepo);
                    Directory.CreateDirectory(folder);
                    BuildUpdates.Install(folder, OfficialRepo, release, say);
                });
                var known = emulators.FirstOrDefault(x => SameFolder(x.Folder, folder));
                if (known != null) _ = Task.Run(() => known.LoadVersion()); // (a new one reads its version as it is added)
                else AddBuild(folder);
                ShowToast("KytyPS5 is ready: add a game and play");
                NoBuildStatus.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                say("The download failed: " + ex.Message);
            }
            finally
            {
                downloading = false;
                foreach (var b in new[] { NoBuildDownload, DownloadBuildBtn }) b.IsEnabled = true;
                UpdateNoBuild();
            }
        }

        // ---- Build updates (BuildUpdates: GitHub releases) -------------------------------------------------------------

        /// <summary>Checks one build off the UI thread; true when it has an update.</summary>
        Task<bool> CheckBuild(EmulatorInstall em)
        {
            if (!em.Updatable)
            {
                em.UpdateStatus = "Updated through the KytyPS5 zipeek scanner";
                return Task.FromResult(false);
            }
            if (!em.Exists) { em.UpdateStatus = "The build is not in its folder"; return Task.FromResult(false); }
            em.UpdateBusy = true;
            em.UpdateStatus = "Checking for updates…";
            return Task.Run(() =>
            {
                string status; BuildUpdates.Release latest = null; var available = false; string repo = null;
                try
                {
                    var installed = BuildUpdates.Read(em.Folder);
                    repo = installed.Repo;
                    if (repo == null) status = "Update source unknown: enter the build's GitHub repository";
                    else
                    {
                        latest = BuildUpdates.Latest(repo);
                        var check = BuildUpdates.Compare(em.Folder, installed, latest);
                        status = check.Status + "  ·  github.com/" + repo;
                        available = check.Available;
                    }
                }
                catch (System.Net.WebException ex) when ((ex.Response as System.Net.HttpWebResponse)?.StatusCode == System.Net.HttpStatusCode.NotFound)
                { status = $"github.com/{repo} has no releases"; }
                catch (Exception ex) { status = "Could not check for updates: " + ex.Message; }
                App.OnUi(() =>
                {
                    em.UpdateRepo = repo;
                    em.LatestRelease = latest;
                    em.UpdateAvailable = available;
                    em.UpdateStatus = status;
                    em.UpdateBusy = false;
                });
                return available;
            });
        }

        async void CheckAllBuilds(bool announce)
        {
            CheckAllBtn.IsEnabled = false;
            var results = await Task.WhenAll(emulators.Select(CheckBuild));
            CheckAllBtn.IsEnabled = true;
            var count = results.Count(r => r);
            if (count > 0) ShowToast(count == 1 ? "An update is available for a KytyPS5 build (Settings → Emulators)" : $"Updates are available for {count} KytyPS5 builds (Settings → Emulators)");
            else if (announce) ShowToast("All KytyPS5 builds are up to date");
        }

        void BuildCheckAll_Click(object sender, RoutedEventArgs e) => CheckAllBuilds(true);

        /// <summary>The library and settings live beside the launcher: say so when that folder takes no files (Program
        /// Files, a read-only drive) instead of losing them quietly.</summary>
        void WarnIfReadOnly()
        {
            try
            {
                var probe = Path.Combine(root, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch
            {
                MessageBox.Show(this, $"The launcher cannot save its library and settings in\n{root}\n\nMove KytyPS5.exe to a folder of its own that you can write to, " +
                    "such as Documents\\KytyPS5 Launcher, and start it from there.", "KytyPS5 Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>The sidebar card: the builds' update state.</summary>
        void UpdateSidebarStatus()
        {
            if (launcherRelease != null)
            {
                SidebarStatus.Text = "Launcher update available";
                SidebarDot.Fill = (Brush)FindResource("AccentBrush");
                SidebarDotGlow.Color = ((SolidColorBrush)SidebarDot.Fill).Color;
                SidebarDotGlow.Opacity = 0.9;
                return;
            }
            if (!emulators.Any(e => e.Exists))
            {
                SidebarStatus.Text = "No emulator yet";
                SidebarDot.Fill = (Brush)FindResource("WarnBrush");
                SidebarDotGlow.Color = ((SolidColorBrush)SidebarDot.Fill).Color;
                SidebarDotGlow.Opacity = 0.9;
                return;
            }
            var updates = emulators.Count(e => e.UpdateAvailable);
            var busy = emulators.Any(e => e.UpdateBusy);
            var unknown = emulators.Any(e => e.NeedsRepo);
            SidebarStatus.Text = busy ? "Checking the builds…"
                : updates > 0 ? (updates == 1 ? "1 build update available" : $"{updates} build updates available")
                : unknown ? "Builds checked; one needs its source"
                : emulators.Any(e => e.UpdateStatus != null) ? "All builds up to date" : "PS5 emulator for PC";
            var colour = updates > 0 ? "AccentBrush" : busy || emulators.All(e => e.UpdateStatus == null) ? "TextMutedBrush" : unknown ? "WarnBrush" : "GoodBrush";
            SidebarDot.Fill = (Brush)FindResource(colour);
            SidebarDotGlow.Color = ((SolidColorBrush)SidebarDot.Fill).Color;
            SidebarDotGlow.Opacity = colour == "TextMutedBrush" ? 0 : 0.9;
        }

        void SidebarCard_Click(object sender, RoutedEventArgs e)
        {
            if (launcherRelease != null) { NavAbout.IsChecked = true; return; }
            NavSettings.IsChecked = true;
            PageSettings.ScrollToTop();
        }

        // ---- The launcher's own updates (SelfUpdate) ------------------------------------------------------------------

        BuildUpdates.Release launcherRelease, launcherLatest;
        bool launcherBusy;

        async void CheckLauncher(bool announce)
        {
            if (launcherBusy) return;
            launcherBusy = true;
            LauncherUpdateBtn.IsEnabled = false;
            LauncherUpdateStatus.Text = "Checking for launcher updates…";
            try
            {
                BuildUpdates.Release latest = null;
                launcherRelease = await Task.Run(() => SelfUpdate.Newer(out latest));
                launcherLatest = latest;
                var current = SelfUpdate.Current;
                var released = SelfUpdate.TagVersion(latest?.Tag);
                if (launcherRelease != null)
                {
                    LauncherUpdateStatus.Text = $"Version {released} is available (you have {current})";
                    LauncherUpdateText.Text = $"Update to {released}";
                    LauncherUpdateBtn.Style = (Style)FindResource("PrimaryButton");
                    LauncherUpdateDot.Fill = (Brush)FindResource("AccentBrush");
                    if (!announce) ShowToast($"KytyPS5 Launcher {released} is available (About)");
                }
                else
                {
                    LauncherUpdateStatus.Text = released != null && released < current
                        ? $"Up to date (this {current} is newer than the latest release, {released})"
                        : $"Up to date (latest release {latest?.Tag})";
                    LauncherUpdateText.Text = "Check for updates";
                    LauncherUpdateBtn.ClearValue(StyleProperty);
                    LauncherUpdateDot.Fill = (Brush)FindResource("GoodBrush");
                    if (announce) ShowToast("The launcher is up to date");
                }
                LauncherNotes.Visibility = latest?.Page != null ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                LauncherUpdateStatus.Text = "Could not check for launcher updates: " + ex.Message;
                LauncherUpdateDot.Fill = (Brush)FindResource("WarnBrush");
            }
            finally
            {
                launcherBusy = false;
                LauncherUpdateBtn.IsEnabled = true;
                NavIcon.SetBadge(NavAbout, launcherRelease != null);
                NavAbout.ToolTip = launcherRelease != null ? "A launcher update is available" : null;
                UpdateSidebarStatus();
            }
        }

        void LauncherNotes_Click(object sender, RoutedEventArgs e)
        {
            if (launcherLatest?.Page != null) OpenUrl(launcherLatest.Page);
        }

        async void LauncherUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (launcherRelease == null) { CheckLauncher(true); return; }
            var release = launcherRelease;
            var answer = MessageBox.Show(this,
                $"Update KytyPS5 Launcher to {SelfUpdate.TagVersion(release.Tag)}?\n\n" +
                $"Downloads {release.Asset} from github.com/{SelfUpdate.Repo}" + (release.Sha256 != null ? ", checks its SHA-256 digest" : "") +
                ", replaces the launcher's own files and restarts it. Your library, settings and emulator builds stay as they are.",
                "Update KytyPS5 Launcher", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            launcherBusy = true;
            LauncherUpdateBtn.IsEnabled = false;
            try
            {
                await Task.Run(() => SelfUpdate.Install(release, text => App.OnUi(() => LauncherUpdateStatus.Text = text)));
                LauncherUpdateStatus.Text = "Restarting…";
                ReadSettings();
                StoreGameSettings();
                settings.Save();
                library.Save();
                App.ReleaseSingle();
                SelfUpdate.Restart(Environment.GetCommandLineArgs().Skip(1).ToArray());
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                launcherBusy = false;
                LauncherUpdateBtn.IsEnabled = true;
                LauncherUpdateStatus.Text = "The update failed: " + ex.Message;
                MessageBox.Show(this, "The launcher was not updated:\n" + ex.Message, "Update KytyPS5 Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        async void BuildCheck_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is EmulatorInstall em) await CheckBuild(em);
        }

        void BuildNotes_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is EmulatorInstall em && em.LatestRelease?.Page != null) OpenUrl(em.LatestRelease.Page);
        }

        async void BuildSource_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is EmulatorInstall em)) return;
            var repo = BuildUpdates.ParseRepo(em.RepoInput);
            if (repo == null)
            {
                MessageBox.Show(this, "Enter the build's GitHub repository: owner/name or its github.com address.", "Update source", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try { BuildUpdates.SaveSource(em.Folder, repo); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Update source", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            em.RepoInput = "";
            await CheckBuild(em);
        }

        void BuildUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is EmulatorInstall em) || em.LatestRelease == null || em.UpdateRepo == null) return;
            var release = em.LatestRelease;
            var answer = MessageBox.Show(this,
                $"Update {em.Name} to {release.Tag}?\n\n" +
                $"Downloads {release.Asset} ({release.Size / 1048576.0:0.#} MB) from github.com/{em.UpdateRepo}" +
                (release.Sha256 != null ? ", checks its SHA-256 digest" : " (GitHub lists no SHA-256 digest for it)") +
                " and replaces the build's program files.\n\nSaves, shader caches and settings stay. The replaced files are kept in " +
                Path.Combine(em.Folder, "_Update", "backup") + ".",
                "Update KytyPS5 build", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            em.UpdateBusy = true;
            var repo = em.UpdateRepo;
            Task.Run(() =>
            {
                try
                {
                    BuildUpdates.Install(em.Folder, repo, release, text => App.OnUi(() => em.UpdateStatus = text));
                    App.OnUi(() =>
                    {
                        em.UpdateBusy = false;
                        em.UpdateAvailable = false;
                        em.UpdateStatus = $"Up to date ({release.Tag}, installed now)  ·  github.com/{repo}";
                        Task.Run(() => em.LoadVersion());
                        foreach (var game in games) LoadGame(game);
                        ShowToast($"{em.Name}: {release.Tag} installed");
                    });
                }
                catch (Exception ex)
                {
                    App.OnUi(() =>
                    {
                        em.UpdateBusy = false;
                        em.UpdateStatus = "Update failed: " + ex.Message;
                        MessageBox.Show(this, "The update was not installed:\n" + ex.Message, "Update KytyPS5 build", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
            });
        }

        void EmulatorRemove_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is EmulatorInstall em) || em.BuiltIn) return;
            emulators.Remove(em);
            library.Emulators.RemoveAll(f => SameFolder(f, em.Folder));
            foreach (var game in games.Where(g => SameFolder(g.Emulator, em.Folder))) game.Emulator = "";
            library.Save();
            foreach (var game in games) LoadGame(game);
            UpdateSelectedGame();
            UpdateNoBuild();
        }

        bool Matches(GameEntry game)
        {
            var q = SearchBox?.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            return game.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (game.TitleId ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void Search_Changed(object sender, TextChangedEventArgs e)
        {
            if (view == null) return;
            view.Refresh();
            UpdateEmpty();
        }

        void Sort_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (view == null) return;
            var keep = Selected;
            view.SortDescriptions.Clear();
            if (SortBox.SelectedIndex == 1) view.SortDescriptions.Add(new SortDescription(nameof(GameEntry.LastPlayed), ListSortDirection.Descending));
            view.SortDescriptions.Add(new SortDescription(nameof(GameEntry.Title), ListSortDirection.Ascending));
            GameList.SelectedItem = keep;
        }

        void View_Checked(object sender, RoutedEventArgs e)
        {
            if (GameList == null) return;
            var tiles = ViewTiles.IsChecked == true;
            GameList.ItemTemplate = (DataTemplate)FindResource(tiles ? "TileTemplate" : "RowTemplate");
            GameList.ItemsPanel = (ItemsPanelTemplate)FindResource(tiles ? "TilePanel" : "RowPanel");
        }

        void UpdateEmpty()
        {
            var none = games.Count == 0;
            var filtered = !none && view.IsEmpty;
            EmptyState.Visibility = none || filtered ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = none ? "No games yet" : "No matching games";
            EmptyText.Text = none ? "Add the game dump: the folder with eboot.bin and sce_sys, or a .zar archive of it." : "Nothing in the library matches \"" + SearchBox.Text.Trim() + "\".";
            EmptyAdd.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        }

        void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Selected != null)
            {
                settings.Game = Selected.Path;
                // The game's own settings into the panel; a game selected for the first time starts from what is shown.
                if (Selected.Settings == null)
                {
                    // (The previous game's add-ons and cheats are its own.)
                    settings.AddCont = "";
                    settings.GamePatch = "";
                    Selected.Settings = settings.ExportGame();
                }
                else settings.ImportGame(Selected.Settings);
                var was = loading;
                loading = true;
                FillSettings();
                loading = was;
                GameList.ScrollIntoView(Selected);
            }
            UpdateSelectedGame();
            if (!loading) { settings.Save(); library.Save(); }
        }

        void StoreGameSettings()
        {
            if (Selected != null) Selected.Settings = settings.ExportGame();
        }

        void GameSettingsMore_Click(object sender, RoutedEventArgs e)
        {
            var menu = GameSettingsMore.ContextMenu;
            menu.PlacementTarget = GameSettingsMore;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        void ApplyAll_Click(object sender, RoutedEventArgs e)
        {
            ReadSettings();
            // A game's add-ons folder and cheat file stay its own.
            foreach (var game in games)
            {
                var own = game.Settings;
                game.Settings = settings.ExportGame();
                foreach (var key in new[] { "addcont", "gamepatch" })
                    game.Settings[key] = own != null && own.TryGetValue(key, out var v) ? v : "";
            }
            settings.Save();
            library.Save();
            ShowToast(games.Count == 1 ? "There is only one game in the library" : $"These settings now apply to all {games.Count} games");
        }

        void ResetGame_Click(object sender, RoutedEventArgs e)
        {
            settings.ResetGame();
            loading = true;
            FillSettings();
            loading = false;
            StoreGameSettings();
            settings.Save();
            library.Save();
            ShowToast(Selected == null ? "Settings reset to defaults" : $"{Selected.Title}: settings reset to defaults");
        }

        void UpdateSelectedGame()
        {
            var game = Selected;
            GameFolder.Text = game?.Path ?? "";
            GameSettingsFor.Text = game == null ? "No game selected" : "Saved for " + game.Title;
            GameFolder.ToolTip = game?.Path;
            if (game == null) GameNote.Text = "Choose the folder with eboot.bin and sce_sys (the game dump), or a .zar archive of it.";
            else if (!game.Exists) GameNote.Text = game.IsZar ? "The .zar archive is missing." : "No eboot.bin in this folder: choose the game dump (eboot.bin, sce_sys).";
            else GameNote.Text = string.IsNullOrEmpty(game.TitleId) ? "" : $"{game.Title} ({game.TitleId} {game.Version})";
            GameNote.Foreground = (Brush)FindResource(game != null && !game.Exists ? "BadBrush" : "TextMutedBrush");
            LaunchButton.IsEnabled = game != null && game.Exists;
            ShaderStatusText.Text = game == null ? "No game selected"
                : !EmulatorFor(game).IsScripted && game.Shaders == ShaderState.Unknown ? $"{game.Title} runs on {EmulatorFor(game).Name}, which keeps its own shader caches"
                : game.Shaders == ShaderState.Unknown ? $"{game.Title}: shader state unknown"
                : $"{game.Title}: {game.ShaderText.ToLowerInvariant()}";
            UpdateDependent();
            UpdateBackdrop();
        }

        const string PrecompileTip = "Every shader and pipeline of the game into the static pipeline cache: hours on a few cores (run it overnight; it resumes when interrupted).";
        const string PrepareTip = "This build compiles every shader and pipeline of the Demon's Souls fork's seeds into this build's cache before the game starts (minutes to an hour, every core; it resumes when interrupted).";
        const string OtherGameTip = "The static precompile reads Demon's Souls' engine files only. Other games are warmed up from recorded play: each shader the game meets is recorded and compiled at the next start.";

        void UpdateBackdrop()
        {
            var source = NavGames.IsChecked == true ? Selected?.Backdrop : null;
            if (Backdrop.Source == source) return;
            // A crossfade: the artwork as it shows now moves to the lower layer and fades out while the new one fades in
            // over it; the scrims stay as long as either shows.
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var duration = TimeSpan.FromMilliseconds(650);
            BackdropOld.BeginAnimation(OpacityProperty, null);
            BackdropOld.Source = Backdrop.Source;
            BackdropOld.Opacity = Backdrop.Opacity;
            BackdropOld.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            Backdrop.BeginAnimation(OpacityProperty, null);
            Backdrop.Opacity = 0;
            Backdrop.Source = source;
            if (source != null) Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            foreach (var scrim in new UIElement[] { BackdropScrim, TopScrim })
                scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(source == null ? 0 : 1, duration) { EasingFunction = ease });
        }

        void GameItem_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
            if (((ListBoxItem)sender).DataContext is GameEntry game) Play(game);
        }

        void GameList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Selected != null) { Play(Selected); e.Handled = true; }
            else if (e.Key == Key.Delete && Selected != null) { RemoveGame(Selected); e.Handled = true; }
        }

        static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T)) d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        void RowPlay_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is GameEntry game) Play(game);
        }

        GameEntry AddGame(string path)
        {
            var known = library.Find(path);
            var game = library.Add(path);
            if (known == null)
            {
                games.Add(game);
                LoadGame(game);
                library.Save();
            }
            if (!string.IsNullOrEmpty(SearchBox.Text) && !Matches(game)) SearchBox.Text = "";
            GameList.SelectedItem = game;
            UpdateEmpty();
            return game;
        }

        void RemoveGame(GameEntry game)
        {
            var index = GameList.SelectedIndex;
            library.Remove(game);
            games.Remove(game);
            library.Save();
            if (GameList.Items.Count > 0) GameList.SelectedIndex = Math.Max(0, Math.Min(index, GameList.Items.Count - 1));
            else { settings.Game = ""; UpdateSelectedGame(); }
            UpdateEmpty();
            ShowToast($"Removed {game.Title} from the library (its files are untouched)");
        }

        string ChooseFolder()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "The game folder (eboot.bin, sce_sys)", ShowNewFolderButton = false })
            {
                var start = Selected?.Path;
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dialog.SelectedPath = start;
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        string ChooseZar()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "The game folder packed into a ZArchive", Filter = "ZArchive (*.zar)|*.zar" };
            var start = Selected?.Path;
            if (!string.IsNullOrEmpty(start) && File.Exists(start)) dialog.InitialDirectory = Path.GetDirectoryName(start);
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        void AddChosen(string path)
        {
            if (path == null) return;
            if (!path.EndsWith(".zar", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(path, "eboot.bin")))
            {
                // A folder above the dump: take the one dump inside it.
                var inner = Directory.Exists(path) ? Directory.GetDirectories(path).Where(d => File.Exists(Path.Combine(d, "eboot.bin"))).ToList() : null;
                if (inner != null && inner.Count == 1) path = inner[0];
                else
                {
                    MessageBox.Show(this, "That folder has no eboot.bin. Choose the game dump: the folder with eboot.bin and sce_sys (for Demon's Souls, PPSA01341-app0).",
                        "KytyPS5", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            var game = AddGame(path);
            ShowToast($"Added {game.Title}");
        }

        void AddGame_Click(object sender, RoutedEventArgs e) => Browse_Click(sender, e);

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var menu = BrowseButton.ContextMenu;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        void BrowseFolder_Click(object sender, RoutedEventArgs e) => AddChosen(ChooseFolder());
        void BrowseZar_Click(object sender, RoutedEventArgs e) => AddChosen(ChooseZar());

        void GameMenu_Opened(object sender, RoutedEventArgs e)
        {
            // The right-clicked row is selected first (ListBox selects on right click); nothing to do without one.
            if (Selected == null) GameMenu.IsOpen = false;
        }

        void MenuPlay_Click(object sender, RoutedEventArgs e) { if (Selected != null) Play(Selected); }
        void MenuRemove_Click(object sender, RoutedEventArgs e) { if (Selected != null) RemoveGame(Selected); }
        void MenuCheck_Click(object sender, RoutedEventArgs e) { if (Selected != null) LoadGame(Selected); }

        void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var game = Selected;
            if (game == null) return;
            if (game.IsZar && File.Exists(game.Path)) Process.Start("explorer.exe", $"/select,\"{game.Path}\"");
            else if (Directory.Exists(game.Path)) Process.Start("explorer.exe", $"\"{game.Path}\"");
        }

        // ---- Settings -----------------------------------------------------------------------------------------------

        void FillSettings()
        {
            Resolution.ItemsSource = Emulator.Resolutions;
            Resolution.SelectedItem = Emulator.Resolutions.Contains(settings.Resolution) ? settings.Resolution : "2560x1440";
            Fullscreen.IsChecked = settings.Fullscreen;
            KeepAspect.IsChecked = settings.Aspect;
            LanguageBox.ItemsSource = Emulator.Languages;
            LanguageBox.SelectedIndex = Math.Max(0, Math.Min(Emulator.Languages.Length - 1, settings.Language));
            RedZone.IsChecked = settings.RedZone;
            Fps120.IsChecked = settings.Fps120;
            VSync.IsChecked = settings.VSync;
            Hdr.IsChecked = settings.Hdr;
            RenderHeight.ItemsSource = Emulator.RenderHeights;
            RenderHeight.SelectedIndex = Math.Max(0, Array.FindIndex(Emulator.RenderHeights, r => r.Split(' ')[0] == settings.Render.ToString()));
            Ratio.ItemsSource = Emulator.Ratios;
            Ratio.SelectedIndex = Math.Max(0, Array.FindIndex(Emulator.Ratios, r => r.Split(' ')[0] == settings.Ratio));
            Quality.ItemsSource = Emulator.Qualities;
            Quality.SelectedIndex = Math.Max(0, Array.FindIndex(Emulator.Qualities, q => q.Split(' ')[0].Equals(settings.Quality, StringComparison.OrdinalIgnoreCase)));
            FrameGen.ItemsSource = Emulator.FrameGens;
            FrameGen.SelectedIndex = emulator.FrameGenAvailable && settings.FrameGen >= 2 ? Math.Min(Emulator.FrameGens.Length - 1, settings.FrameGen - 1) : 0;
            Overlay.ItemsSource = Emulator.Overlays;
            Overlay.SelectedIndex = Math.Max(0, Math.Min(Emulator.Overlays.Length - 1, settings.Hud));
            ECores.IsChecked = settings.ECores && emulator.EfficiencyMask != 0;
            ECores.IsEnabled = emulator.EfficiencyMask != 0;
            if (emulator.EfficiencyMask == 0) ECores.ToolTip = "This processor has no efficiency cores.";
            PrecompilePrompt.IsChecked = settings.PrecompilePrompt;
            CloseOnPlay.IsChecked = settings.CloseOnPlay;
            CheckUpdates.IsChecked = settings.CheckUpdates;

            // The game's options of the upstream launcher.
            SafeGpu.IsChecked = settings.SafeGpu;
            AmdCpu.IsChecked = settings.AmdCpu;
            SyncRaw.IsChecked = settings.SyncRawImages;
            ZMask.IsChecked = settings.ZMask;
            ReadbackLinear.IsChecked = settings.ReadbackLinear;
            Tessellation.IsChecked = settings.Tessellation;
            SkipLogos.IsChecked = settings.SkipLogos;
            EbootPrecompile.IsChecked = settings.EbootPrecompile;
            ShowPath(AddCont, settings.AddCont);
            ShowPath(GamePatch, settings.GamePatch);

            // The launcher's options for every game.
            UserName.Text = settings.UserName;
            UserId.Text = settings.UserId.ToString();
            Lightbar.Text = settings.Lightbar;
            Vibration.ItemsSource = Percentages;
            Vibration.SelectedIndex = Math.Max(0, Math.Min(10, settings.Vibration / 10));
            SpeakerVolume.ItemsSource = Percentages;
            SpeakerVolume.SelectedIndex = Math.Max(0, Math.Min(10, settings.SpeakerVolume / 10));
            KeyboardPlayer.ItemsSource = KeyboardPlayers;
            KeyboardPlayer.SelectedIndex = Math.Max(0, Math.Min(4, settings.KeyboardPlayer));
            // The cards as Vulkan lists them (the emulator's --gpu index); a saved index no longer there is kept and marked.
            var gpus = new List<string> { "Auto (the emulator chooses)" };
            gpus.AddRange(GpuList.Select(d => $"{d.Name} ({d.Kind})"));
            for (var i = GpuList.Count; i <= settings.Gpu; i++) gpus.Add($"Device {i} (not found)");
            GpuBox.ItemsSource = gpus;
            GpuBox.SelectedIndex = Math.Max(0, Math.Min(gpus.Count - 1, settings.Gpu + 1));
            TrophyDuration.ItemsSource = TrophySeconds;
            TrophyDuration.SelectedIndex = Math.Max(0, Math.Min(5, settings.TrophyDuration - 5));
            Trophies.IsChecked = settings.Trophies;
            HideCursor.IsChecked = settings.HideCursor;
            Network.IsChecked = settings.Network;
            VulkanValidation.IsChecked = settings.VulkanValidation;
            ShaderValidation.IsChecked = settings.ShaderValidation;
            RenderDoc.IsChecked = settings.RenderDoc;
            UpdateDependent();
        }

        static readonly string[] Percentages = Enumerable.Range(0, 11).Select(i => i * 10 + "%").ToArray();
        static readonly string[] KeyboardPlayers = { "Shared with player 1 (default)", "Player 1", "Player 2", "Player 3", "Player 4" };
        static List<VulkanDevices.Device> gpuList;
        static List<VulkanDevices.Device> GpuList => gpuList ?? (gpuList = VulkanDevices.List());
        static readonly string[] TrophySeconds = Enumerable.Range(5, 6).Select(i => i + " s" + (i == 6 ? " (default)" : "")).ToArray();

        static void ShowPath(TextBox box, string path)
        {
            box.Text = path ?? "";
            box.ToolTip = string.IsNullOrEmpty(path) ? null : path;
            box.Foreground = (Brush)Application.Current.FindResource(!string.IsNullOrEmpty(path) && !File.Exists(path) && !Directory.Exists(path) ? "BadBrush" : "TextSecondaryBrush");
        }

        readonly Dictionary<Control, object> ownTips = new Dictionary<Control, object>();

        void UpdateDependent()
        {
            // A build without the scripts takes only kyty_emulator.exe's own options.
            var build = EmulatorFor(Selected);
            var scripted = build.IsScripted;
            KeepAspect.IsEnabled = scripted && Fullscreen.IsChecked == true;
            FrameGen.IsEnabled = scripted && emulator.FrameGenAvailable;
            // An option the build does not take is greyed out (and not passed); until its --help is read, all are offered.
            bool takes(string option) => !build.Probed || build.Supports(option);
            var missing = new List<string>();
            void offer(Control c, bool ok, string name)
            {
                if (!ownTips.ContainsKey(c)) ownTips[c] = c.ToolTip;
                c.IsEnabled = ok;
                c.ToolTip = ok ? ownTips[c] : "Not in " + build.Name;
                ToolTipService.SetShowOnDisabled(c, true);
                if (!ok) missing.Add(name);
            }
            var perfOverlay = !scripted && takes("--perf-overlay");
            Overlay.IsEnabled = scripted || perfOverlay;
            OverlayOptions.Visibility = scripted || perfOverlay ? Visibility.Visible : Visibility.Collapsed;
            OverlayNote.Text = scripted ? "top right: frame rate and frame times; detailed: GPU, CPU, memory"
                : perfOverlay ? "the emulator's performance panel (F2; Shift+F2 detailed)" : "not in this emulator";
            offer(Fullscreen, takes("--fullscreen"), "fullscreen");
            offer(Hdr, takes("--hdr"), "HDR");
            offer(RedZone, takes("--redzone"), "red zone");
            offer(VSync, takes("--present-mode"), "VSync");
            offer(Fps120, takes("--vblank-frequency"), "120 fps");
            offer(SkipLogos, takes("--skip-notice-screen"), "skip boot logos");
            offer(SafeGpu, !build.Probed || build.SafeGpuSwitch != null, "Safe GPU mode");
            offer(AmdCpu, takes("--amd-cpu"), "AMD CPU patch");
            offer(SyncRaw, takes("--sync-raw-image-buffers"), "accurate texture synchronization");
            offer(ZMask, takes("--support-zmask"), "ZMask");
            offer(ReadbackLinear, takes("--readback-linear-images"), "read back linear images");
            offer(Tessellation, takes("--tessellation"), "tessellation");
            CompatNote.Text = missing.Count == 0 ? "" : $"Not in {build.Name}: " + string.Join(", ", missing) + ".";
            var addons = takes("--addcont-dir");
            var cheats = takes("--game-patch");
            foreach (var c in new Control[] { AddCont, AddContPick, AddContClear }) c.IsEnabled = addons;
            foreach (var c in new Control[] { GamePatch, GamePatchPick, GamePatchClear }) c.IsEnabled = cheats;
            ContentNote.Text = addons && cheats ? "" : !addons && !cheats ? $"Not in {build.Name}: add-ons, cheats." : !addons ? $"Not in {build.Name}: add-ons." : $"Not in {build.Name}: cheats.";
            foreach (var note in new[] { CompatNote, ContentNote }) note.Visibility = note.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            // KytyPS5 zipeek's experimental eboot precompile: shown only for a game on a build that has it.
            var eboot = build.HasSwitch("KYTY_EBOOT_PRECOMPILE") ? Visibility.Visible : Visibility.Collapsed;
            EbootPrecompile.Visibility = eboot;
            EbootNote.Visibility = eboot;
            // Render height, aspect ratio and graphics are Demon's Souls' own (run-windows.ps1); the static precompile reads
            // its engine's files only.
            var des = (Selected?.IsDemonsSouls ?? true) && scripted;
            // A build's own extras are shown only where they work (the official launcher's options stay, greyed, below).
            DesOptions.Visibility = des ? Visibility.Visible : Visibility.Collapsed;
            FrameGenOptions.Visibility = scripted ? Visibility.Visible : Visibility.Collapsed;
            KeepAspect.Visibility = scripted ? Visibility.Visible : Visibility.Collapsed;
            foreach (var c in new Control[] { RenderHeight, Ratio, Quality }) c.IsEnabled = des;
            foreach (var t in new[] { RenderNote, RatioNote, QualityNote }) t.Visibility = des ? Visibility.Visible : Visibility.Collapsed;
            foreach (var t in new[] { RenderOnly, RatioOnly, QualityOnly })
            {
                t.Visibility = des ? Visibility.Collapsed : Visibility.Visible;
                t.Text = scripted ? "Demon's Souls only: not used for this game" : "not used by this emulator";
            }
            var was = loading;
            loading = true;
            EmulatorBox.SelectedItem = build;
            loading = was;
            EmulatorBox.IsEnabled = Selected != null;
            EmulatorNote.Text = !build.Exists ? "No KytyPS5 emulator yet: download it with the button above the game list"
                : !scripted && build.PresetFile != null ? $"its {System.IO.Path.GetFileName(build.PresetFile)} (as its own launcher) and the options below it takes"
                : !scripted ? "kyty_emulator.exe's own command line: the options below it takes"
                : build.BuiltIn ? "every option below; shaders precompiled for Demon's Souls" : "a Demon's Souls build: its scripts, Demon's Souls' render options and frame generation";
            // KytyPS5 zipeek: a game with the Demon's Souls fork's shader seeds is prepared by the build itself.
            var prepare = !scripted && build.ForkSeeds(Selected?.TitleId) != null;
            PanelPrecompile.IsEnabled = des || prepare;
            PageHints.Visibility = des ? Visibility.Visible : Visibility.Collapsed;
            PanelPrecompile.ToolTip = des ? PrecompileTip : prepare ? PrepareTip : OtherGameTip;
            PrecompileOther.Visibility = des || prepare ? Visibility.Collapsed : Visibility.Visible;
            FrameGenNote.Text = !scripted ? "not used by this emulator"
                : emulator.FrameGenAvailable ? "DLSS; NVIDIA RTX 40 or newer (3x and up: RTX 50)" : "needs NVIDIA's Streamline SDK: choose its folder in Settings";
            StreamlineStatus.Text = emulator.FrameGenAvailable ? "NVIDIA Streamline runtime: " + emulator.StreamlineDir
                : string.IsNullOrEmpty(settings.Streamline) ? "No Streamline runtime chosen: frame generation is off"
                : "No NVIDIA-signed sl.interposer.dll in " + settings.Streamline;
            StreamlineDot.Fill = (Brush)FindResource(emulator.FrameGenAvailable ? "GoodBrush" : "TextMutedBrush");
        }

        void ReadSettings()
        {
            settings.Resolution = (string)Resolution.SelectedItem ?? "2560x1440";
            settings.Fullscreen = Fullscreen.IsChecked == true;
            settings.Aspect = KeepAspect.IsChecked == true;
            settings.Language = Math.Max(0, LanguageBox.SelectedIndex);
            settings.RedZone = RedZone.IsChecked == true;
            settings.Fps120 = Fps120.IsChecked == true;
            settings.VSync = VSync.IsChecked == true;
            settings.Hdr = Hdr.IsChecked == true;
            settings.Hud = Math.Max(0, Overlay.SelectedIndex);
            settings.Render = int.Parse(((string)RenderHeight.SelectedItem ?? "1440").Split(' ')[0]);
            settings.Ratio = ((string)Ratio.SelectedItem ?? "16:9").Split(' ')[0];
            settings.Quality = ((string)Quality.SelectedItem ?? "Standard").Split(' ')[0].ToLowerInvariant();
            // (Kept as it was where the runtime is missing.)
            if (emulator.FrameGenAvailable) settings.FrameGen = FrameGen.SelectedIndex > 0 ? FrameGen.SelectedIndex + 1 : 0;
            settings.ECores = ECores.IsChecked == true;
            settings.CloseOnPlay = CloseOnPlay.IsChecked == true;
            settings.CheckUpdates = CheckUpdates.IsChecked == true;
            settings.SafeGpu = SafeGpu.IsChecked == true;
            settings.AmdCpu = AmdCpu.IsChecked == true;
            settings.SyncRawImages = SyncRaw.IsChecked == true;
            settings.ZMask = ZMask.IsChecked == true;
            settings.ReadbackLinear = ReadbackLinear.IsChecked == true;
            settings.Tessellation = Tessellation.IsChecked == true;
            settings.SkipLogos = SkipLogos.IsChecked == true;
            settings.EbootPrecompile = EbootPrecompile.IsChecked == true;
            settings.AddCont = AddCont.Text;
            settings.GamePatch = GamePatch.Text;
            // The profile as the upstream launcher checks it: a name of 1-16 UTF-8 bytes, an ID other than 254 and 255.
            var name = System.Text.RegularExpressions.Regex.Replace(UserName.Text, "[\"%^&|<>]", "").Trim();
            settings.UserName = Encoding.UTF8.GetByteCount(name) <= 16 ? name : settings.UserName;
            if (int.TryParse(UserId.Text.Trim(), out var id) && id > 0 && id != 254 && id != 255) settings.UserId = id;
            UserIdNote.Foreground = (Brush)FindResource(UserId.Text.Trim() == settings.UserId.ToString() ? "TextMutedBrush" : "BadBrush");
            var color = Lightbar.Text.Trim();
            if (color.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")) settings.Lightbar = color.ToUpperInvariant();
            settings.Vibration = Math.Max(0, Vibration.SelectedIndex) * 10;
            settings.SpeakerVolume = Math.Max(0, SpeakerVolume.SelectedIndex) * 10;
            settings.KeyboardPlayer = Math.Max(0, KeyboardPlayer.SelectedIndex);
            settings.Gpu = GpuBox.SelectedIndex - 1;
            settings.TrophyDuration = Math.Max(0, TrophyDuration.SelectedIndex) + 5;
            settings.Trophies = Trophies.IsChecked == true;
            settings.HideCursor = HideCursor.IsChecked == true;
            settings.Network = Network.IsChecked == true;
            settings.VulkanValidation = VulkanValidation.IsChecked == true;
            settings.ShaderValidation = ShaderValidation.IsChecked == true;
            settings.RenderDoc = RenderDoc.IsChecked == true;
            if (Selected != null) settings.Game = Selected.Path;
        }

        void Lightbar_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = Lightbar.Text.Trim();
            LightbarSwatch.Background = System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$")
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(text)) : Brushes.Transparent;
        }

        void PickPath(TextBox box, bool folder, string title, string filter)
        {
            string chosen = null;
            if (folder)
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = title, ShowNewFolderButton = false })
                {
                    if (Directory.Exists(box.Text)) dialog.SelectedPath = box.Text;
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) chosen = dialog.SelectedPath;
                }
            }
            else
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
                if (File.Exists(box.Text)) dialog.InitialDirectory = Path.GetDirectoryName(box.Text);
                if (dialog.ShowDialog(this) == true) chosen = dialog.FileName;
            }
            if (chosen == null) return;
            ShowPath(box, chosen);
            Setting_Changed(box, null);
        }

        void AddCont_Click(object sender, RoutedEventArgs e) => PickPath(AddCont, true, "The folder with the game's extracted add-on (DLC) packages", null);
        void GamePatch_Click(object sender, RoutedEventArgs e) => PickPath(GamePatch, false, "An ETAHen cheat file for this game", "ETAHen cheats (*.json)|*.json|All files (*.*)|*.*");
        void AddContClear_Click(object sender, RoutedEventArgs e) { ShowPath(AddCont, ""); Setting_Changed(sender, e); }
        void GamePatchClear_Click(object sender, RoutedEventArgs e) { ShowPath(GamePatch, ""); Setting_Changed(sender, e); }

        void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            UpdateDependent();
            ReadSettings();
            StoreGameSettings();
            settings.Save();
            library.Save();
        }

        // ---- Settings kept in the builds' folders ---------------------------------------------------------------------

        string ZipeekPresetPath => Path.Combine(root, "zipeek-preset.json");

        /// <summary>Launcher 1.1 turned KytyPS5 zipeek's eboot precompile on for every game in zipeek-preset.json; it is a
        /// game's option now (Game Settings): a preset that has it on hands it to every game and is turned off.</summary>
        void MigrateEbootPrecompile()
        {
            try
            {
                if (!File.Exists(ZipeekPresetPath)) return;
                var preset = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(ZipeekPresetPath));
                if (preset == null || !preset.TryGetValue("KYTY_EBOOT_PRECOMPILE", out var on) || Convert.ToString(on) != "1") return;
                preset["KYTY_EBOOT_PRECOMPILE"] = "0";
                preset["KYTY_DYNAMIC_BUFFER_STRIDES"] = "0";
                File.WriteAllText(ZipeekPresetPath, Core.Json.Pretty(preset));
                foreach (var game in games)
                {
                    if (game.Settings == null) game.Settings = settings.ExportGame();
                    game.Settings["ebootprecompile"] = true;
                }
                settings.EbootPrecompile = true;
                library.Save();
            }
            catch { }
        }

        /// <summary>The files that turn the before-playing precompile offer off: no-precompile-prompt.txt of every Demon's
        /// Souls build (run-windows.ps1 -Prompt reads it) and of KytyPS5 zipeek.</summary>
        IEnumerable<string> PromptFiles() => emulators.Where(e => e.IsScripted || e.ZipeekPresetFile != null)
            .Select(e => Path.Combine(e.Folder, "no-precompile-prompt.txt"));

        /// <summary>The offer is one setting for every game: kept in launcher-settings.json and written to every build's
        /// file. A launcher without the key takes it from the files (off where any build has it off).</summary>
        void SyncPrecompilePrompt(bool fromFiles)
        {
            if (fromFiles)
            {
                settings.PrecompilePrompt = !PromptFiles().Any(File.Exists);
                return;
            }
            foreach (var file in PromptFiles())
            {
                try
                {
                    if (settings.PrecompilePrompt) { if (File.Exists(file)) File.Delete(file); }
                    else if (!File.Exists(file)) File.WriteAllText(file, "no precompile offer before playing (delete this file to get it back)\r\n");
                }
                catch { }
            }
        }

        void PrecompilePrompt_Click(object sender, RoutedEventArgs e)
        {
            settings.PrecompilePrompt = PrecompilePrompt.IsChecked == true;
            SyncPrecompilePrompt(false);
            settings.Save();
        }

        void Streamline_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = $"The unpacked NVIDIA Streamline SDK ({Emulator.StreamlineSdkUrl}), or its bin\\x64 folder" })
            {
                if (!string.IsNullOrEmpty(settings.Streamline) && Directory.Exists(settings.Streamline)) dialog.SelectedPath = settings.Streamline;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                if (!emulator.SetStreamline(dialog.SelectedPath))
                {
                    MessageBox.Show(this, $"No sl.interposer.dll signed by NVIDIA in that folder or its bin\\x64.\nDownload the Streamline SDK from {Emulator.StreamlineSdkUrl} and choose the unpacked folder.",
                        "Frame generation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                settings.Save();
                UpdateDependent();
                ShowToast("Frame generation is available");
            }
        }

        static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        void StreamlineDownload_Click(object sender, RoutedEventArgs e) => OpenUrl(Emulator.StreamlineSdkUrl);
        void Repo_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/" + SelfUpdate.Repo);


        // ---- Actions ------------------------------------------------------------------------------------------------

        /// <summary>The Demon's Souls build for a script action, or a message where there is none.</summary>
        Emulator Scripted()
        {
            var build = ScriptedBuild();
            if (build == null) MessageBox.Show(this, "No Demon's Souls build (with run-windows.ps1) in the emulator list: add one in Settings → Emulators.", "KytyPS5", MessageBoxButton.OK, MessageBoxImage.Information);
            return build;
        }

        void Play(GameEntry game)
        {
            if (game == null) return;
            if (!game.Exists)
            {
                MessageBox.Show(this, "The game is not where the library has it:\n" + game.Path, "KytyPS5", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            GameList.SelectedItem = game;
            ReadSettings();
            StoreGameSettings();
            settings.Game = game.Path;
            settings.Save();
            game.LastPlayed = DateTime.Now;
            library.Save();
            var build = EmulatorFor(game);
            if (!emulators.Any(x => x.Exists))
            {
                MessageBox.Show(this, "There is no KytyPS5 emulator yet. Download it with the button above the game list (or add a KytyPS5 folder you have in Settings → Emulators), then play.",
                    "KytyPS5", MessageBoxButton.OK, MessageBoxImage.Information);
                NavGames.IsChecked = true;
                return;
            }
            if (!build.Exists)
            {
                MessageBox.Show(this, "The emulator is not where the list has it:\n" + build.Exe, "KytyPS5", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // KytyPS5 zipeek: shaders not prepared for this build yet - offer "Prepare shaders" first, then the game.
            if (!build.IsScripted && build.NeedsPrepare(game.TitleId) && settings.PrecompilePrompt)
            {
                var answer = MessageBox.Show(this,
                    $"The shaders of {game.Title} are not prepared for this build yet, so the game would stutter while it compiles them.\n\n" +
                    "Prepare them now? Every core compiles them in a console window (minutes to an hour; an interrupted run resumes), then the game starts.\n\n" +
                    "Yes: prepare, then play.   No: play now.",
                    "Prepare shaders", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.Yes)
                {
                    PrepareShaders(game, build, true);
                    return;
                }
            }
            try
            {
                if (build.BuiltIn && build.IsScripted) emulator.Play(game.IsDemonsSouls);
                else if (build.IsScripted) new Emulator(build.Folder, settings).Play(game.IsDemonsSouls);
                else build.PlayDirect(settings, game.Path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "KytyPS5", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            if (settings.CloseOnPlay) Close();
            else ShowToast($"Starting {game.Title}…");
        }

        void Launch_Click(object sender, RoutedEventArgs e) => Play(Selected);

        /// <summary>KytyPS5 zipeek: "Prepare shaders" for a game on the build (EmulatorInstall.PrepareDirect) in its own console
        /// window; when it finished, the game starts if <paramref name="play"/>.</summary>
        void PrepareShaders(GameEntry game, EmulatorInstall build, bool play)
        {
            ReadSettings();
            settings.Save();
            Process process;
            try { process = build.PrepareDirect(settings, game.Path); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "KytyPS5", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            ShowToast("Preparing shaders in their own window…");
            Task.Run(() =>
            {
                process.WaitForExit();
                var code = process.ExitCode;
                App.OnUi(() =>
                {
                    if (code != 0)
                    {
                        MessageBox.Show(this, $"Preparing the shaders ended early (exit code {code}). The next run resumes where it stopped.",
                            "Prepare shaders", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    ShowToast("Shaders prepared");
                    if (!play) return;
                    try { build.PlayDirect(settings, game.Path); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "KytyPS5", MessageBoxButton.OK, MessageBoxImage.Error); }
                });
            });
        }

        void Precompile_Click(object sender, RoutedEventArgs e)
        {
            var zipeek = EmulatorFor(Selected);
            if (Selected != null && !zipeek.IsScripted && zipeek.ForkSeeds(Selected.TitleId) != null)
            {
                PrepareShaders(Selected, zipeek, false);
                return;
            }
            var des = Scripted();
            if (des == null) return;
            ReadSettings();
            settings.Save();
            des.Precompile();
            ShowToast("Precompile started in its own window");
        }

        void ExportHints_Click(object sender, RoutedEventArgs e)
        {
            var des = Scripted();
            if (des == null) return;
            ReadSettings();
            settings.Save();
            des.ExportHints();
        }

        /// <summary>The readme of the selected game's build (Game Settings), else the launcher's own.</summary>
        void Readme_Click(object sender, RoutedEventArgs e)
        {
            var build = EmulatorFor(Selected);
            var file = (build.Exists ? new Emulator(build.Folder, settings).ReadmeFile : null) ?? LauncherReadme;
            if (file != null) Process.Start("notepad.exe", $"\"{file}\"");
        }

        static string LauncherReadme
        {
            get
            {
                var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KytyPS5 Launcher - README.txt");
                return File.Exists(file) ? file : null;
            }
        }

        void LauncherReadme_Click(object sender, RoutedEventArgs e)
        {
            if (LauncherReadme != null) Process.Start("notepad.exe", $"\"{LauncherReadme}\"");
        }

        void License_Click(object sender, RoutedEventArgs e)
        {
            // The launcher's own licence (GPL-2.0) beside it, else KytyPS5's on GitHub.
            var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KytyPS5 Launcher - LICENSE.txt");
            if (File.Exists(file)) Process.Start("notepad.exe", $"\"{file}\"");
            else OpenUrl(Emulator.RepositoryUrl + "/blob/main/LICENSE");
        }

        void OpenRoot_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{root}\"");

        void DesktopShortcut_Click(object sender, RoutedEventArgs e) =>
            MakeShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "on the desktop");

        void StartShortcut_Click(object sender, RoutedEventArgs e) =>
            MakeShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "in the Start menu");

        void MakeShortcut(string folder, string where)
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                Shortcuts.Create(Path.Combine(folder, "KytyPS5.lnk"), exe, Path.GetDirectoryName(exe), "KytyPS5 – PS5 emulator launcher");
                ShortcutNote.Text = "KytyPS5 shortcut created " + where + ".";
                ShortcutNote.Visibility = Visibility.Visible;
                ShowToast("Shortcut created " + where);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "KytyPS5", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        DispatcherTimer toastTimer;

        void ShowToast(string text)
        {
            ToastText.Text = text;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            toastTimer?.Stop();
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
            toastTimer.Tick += (s, e) => { toastTimer.Stop(); Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))); };
            toastTimer.Start();
        }

        // ---- Compatibility ------------------------------------------------------------------------------------------

        ListCollectionView compatView;

        /// <summary>Checks the list off the UI thread; a change is shown at once (and announced unless asked for).</summary>
        void CheckCompat(bool asked)
        {
            if (compatChecking) return;
            compatChecking = true;
            CompatRefresh.IsEnabled = false;
            CompatUpdated.Text = "Checking the list…";
            var before = compat.Games.Count;
            Task.Run(() => compat.Refresh()).ContinueWith(t => App.OnUi(() =>
            {
                compatChecking = false;
                CompatRefresh.IsEnabled = true;
                var changed = !t.IsFaulted && t.Result;
                if (changed) ShowCompat();
                else UpdateCompatStatus();
                if (changed && before > 0)
                {
                    var fresh = compat.Games.Count(g => g.Change == "new");
                    var better = compat.Games.Count(g => g.Change == "improved");
                    ShowToast(fresh + better > 0 ? $"Compatibility list updated: {fresh} new, {better} improved" : "Compatibility list updated");
                }
                else if (asked && compat.LastError == null) ShowToast("The compatibility list is up to date");
            }));
        }

        void ShowCompat()
        {
            compat.MarkLibrary(games);
            compatView = new ListCollectionView(compat.Games) { Filter = o => CompatMatches((CompatGame)o) };
            ApplyCompatSort();
            CompatList.ItemsSource = compatView;
            int n(string status) => compat.Games.Count(g => g.Status == status);
            StatAll.Tag = compat.Games.Count.ToString();
            StatInGame.Tag = n("in-game").ToString();
            StatMenu.Tag = n("main-menu").ToString();
            StatLogo.Tag = n("logo").ToString();
            StatNoBoot.Tag = n("doesnt-boot").ToString();
            BarInGame.Width = new GridLength(n("in-game"), GridUnitType.Star);
            BarMenu.Width = new GridLength(n("main-menu"), GridUnitType.Star);
            BarLogo.Width = new GridLength(n("logo"), GridUnitType.Star);
            BarNoBoot.Width = new GridLength(n("doesnt-boot"), GridUnitType.Star);
            UpdateCompatStatus();
            UpdateCompatEmpty();
        }

        void UpdateCompatStatus()
        {
            string when(DateTime utc) => utc == default ? "never" : utc.ToLocalTime().Date == DateTime.Today ? utc.ToLocalTime().ToString("HH:mm") : utc.ToLocalTime().ToString("d. M. HH:mm");
            var offline = compat.LastError != null;
            CompatLive.Fill = (Brush)FindResource(offline ? "WarnBrush" : "GoodBrush");
            CompatUpdated.Text = offline
                ? $"Offline ({compat.LastError}): the list from {when(compat.LastChanged)}"
                : $"Live from kytyps5.github.io · checked {when(compat.LastChecked)} · list changed {when(compat.LastChanged)} · checks every 10 minutes";
        }

        bool CompatMatches(CompatGame g)
        {
            if (StatInGame.IsChecked == true && g.Status != "in-game") return false;
            if (StatMenu.IsChecked == true && g.Status != "main-menu") return false;
            if (StatLogo.IsChecked == true && g.Status != "logo") return false;
            if (StatNoBoot.IsChecked == true && g.Status != "doesnt-boot") return false;
            if (CompatMine.IsChecked == true && !g.InLibrary) return false;
            if (CompatChanged.IsChecked == true && g.Change == null) return false;
            var q = CompatSearch.Text.Trim();
            return q.Length == 0 || (g.Title ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (g.TitleId ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void ApplyCompatSort()
        {
            if (compatView == null) return;
            compatView.SortDescriptions.Clear();
            switch (CompatSort.SelectedIndex)
            {
                case 1:
                    compatView.SortDescriptions.Add(new SortDescription(nameof(CompatGame.Rank), ListSortDirection.Descending));
                    compatView.SortDescriptions.Add(new SortDescription(nameof(CompatGame.LatestTested), ListSortDirection.Descending));
                    break;
                case 2:
                    compatView.SortDescriptions.Add(new SortDescription(nameof(CompatGame.Title), ListSortDirection.Ascending));
                    break;
                default:
                    compatView.SortDescriptions.Add(new SortDescription(nameof(CompatGame.LatestTested), ListSortDirection.Descending));
                    compatView.SortDescriptions.Add(new SortDescription(nameof(CompatGame.Title), ListSortDirection.Ascending));
                    break;
            }
        }

        void UpdateCompatEmpty()
        {
            var empty = compatView == null || compatView.IsEmpty;
            CompatEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            CompatEmpty.Text = compat.Games.Count == 0
                ? (compat.LastError != null ? "The compatibility list could not be downloaded: " + compat.LastError : "Downloading the compatibility list…")
                : "No game matches these filters.";
        }

        void CompatFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (compatView == null) return;
            compatView.Refresh();
            UpdateCompatEmpty();
        }

        void CompatSearch_Changed(object sender, TextChangedEventArgs e) => CompatFilter_Changed(sender, e);

        void CompatSort_Changed(object sender, SelectionChangedEventArgs e)
        {
            ApplyCompatSort();
            compatView?.Refresh();
        }

        void CompatRefresh_Click(object sender, RoutedEventArgs e) => CheckCompat(true);
        void CompatSite_Click(object sender, RoutedEventArgs e) => OpenUrl(Compatibility.SiteUrl);
        void CompatOpen_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).Tag is string url) OpenUrl(url); }

        // ---- Logs ---------------------------------------------------------------------------------------------------

        sealed class LogFile
        {
            public string Path { get; set; }
            public string Name { get; set; }
            public string Details { get; set; }
            public string Glyph { get; set; }
            public Brush Brush { get; set; }
        }

        void LogsPage_Click(object sender, RoutedEventArgs e) => NavLogs.IsChecked = true;

        /// <summary>Whose logs the Logs page shows: the selected game's Demon's Souls build (run-windows.ps1 writes them), else
        /// the home build's (KytyPS5 zipeek's play logs).</summary>
        Emulator LogsOfSelected() => EmulatorFor(Selected).IsScripted ? ScriptedBuild() ?? emulator : emulator;
        void LogsRefresh_Click(object sender, RoutedEventArgs e) => LoadLogs();
        void LogsFolder_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{LogsOfSelected().LogsFolder}\"");

        void LogOpen_Click(object sender, RoutedEventArgs e)
        {
            if (LogList.SelectedItem is LogFile log && File.Exists(log.Path)) Process.Start("notepad.exe", $"\"{log.Path}\"");
        }

        static string Size(long bytes) => bytes < 1024 ? bytes + " B" : bytes < 1 << 20 ? (bytes / 1024.0).ToString("0.#") + " KB" : (bytes / 1048576.0).ToString("0.#") + " MB";

        void LoadLogs()
        {
            // The logs run-windows.ps1 writes: the Demon's Souls build's (a build started directly logs to its own console).
            var folder = LogsOfSelected().LogsFolder;
            var keep = (LogList.SelectedItem as LogFile)?.Path;
            var errBrush = (Brush)FindResource("WarnBrush");
            var outBrush = (Brush)FindResource("AccentTextBrush");
            var files = new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTime).Take(300)
                .Select(f => new LogFile
                {
                    Path = f.FullName,
                    Name = f.FullName.Substring(folder.Length).TrimStart('\\'),
                    Details = f.LastWriteTime.ToString("d. M. yyyy  HH:mm") + "   •   " + Size(f.Length),
                    Glyph = f.Name.Contains(".err") ? "" : "",
                    Brush = f.Name.Contains(".err") ? errBrush : outBrush,
                }).ToList();
            LogList.ItemsSource = files;
            NoLogs.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LogList.SelectedItem = files.FirstOrDefault(f => f.Path == keep) ?? files.FirstOrDefault(f => !f.Name.Contains(".err") && f.Name.EndsWith(".log")) ?? files.FirstOrDefault();
            if (files.Count == 0) { LogTitle.Text = folder; LogView.Text = ""; }
        }

        void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(LogList.SelectedItem is LogFile log)) return;
            LogTitle.Text = log.Path;
            try
            {
                // The end of a long log (the last 512 KB), read while the emulator may still write it.
                using (var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    const int limit = 512 * 1024;
                    var cut = stream.Length > limit;
                    if (cut) stream.Seek(-limit, SeekOrigin.End);
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        var text = reader.ReadToEnd();
                        if (cut) text = "… (the last 512 KB)\n" + text.Substring(Math.Max(0, text.IndexOf('\n') + 1));
                        LogView.Text = text.Length == 0 ? "(empty)" : text;
                    }
                }
                LogView.ScrollToEnd();
            }
            catch (Exception ex) { LogView.Text = ex.Message; }
        }
    }

    /// <summary>.lnk files through the shell's IShellLink.</summary>
    static class Shortcuts
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr data, int flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        public static void Create(string lnk, string target, string workingDirectory, string description)
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnk, true);
        }
    }
}
