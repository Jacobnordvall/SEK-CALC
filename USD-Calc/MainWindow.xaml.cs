using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.Storage;
using WinRT.Interop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;


// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace USD_Calc
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        // remember last clipboard text we applied so we don't reapply the same value repeatedly
        private string _lastClipboardTextApplied = string.Empty;
        // suppress InputBox TextChanged handler when programmatically updating the input
        private bool _suppressInputTextChanged = false;
        // prevent reentrant/duplicate handling of the copy action
        private bool _isHandlingCopy = false;
        // last numeric input that produced a result (used to avoid double-counting)
        private decimal? _lastComputedInputValue = null;
        // persisted count of how many unique copies were made
        private long _uniqueCopyCount = 0;
        // last computed input value that has been copied to clipboard (to avoid double-counting copies for same math run)
        private decimal? _lastCopiedInputValue = null;
        // diagnostic text for persistence attempts (shown in settings)
        private string _persistenceDiag = string.Empty;
        // no custom folder token; use AppData (LocalLow/Local/Roaming) locations only
        // seconds per run used to compute hours saved
        private int _secondsPerRun = 10; // no-op placeholder
        private System.IO.FileSystemWatcher? _settingsWatcher = null;
        private DateTime _lastSettingsWriteUtc = DateTime.MinValue;

        // Increment and persist the unique-copy counter (called when a copy-to-clipboard actually occurs)
        private void IncrementAndPersistUniqueCopyCount()
        {
            try
            {
                _uniqueCopyCount++;
                try
                {
                    var local = ApplicationData.Current.LocalSettings;
                    local.Values["UniqueCopyCount"] = _uniqueCopyCount.ToString(CultureInfo.InvariantCulture);
                }
                catch { }
                // persist settings to single JSON file
                try { SaveSettingsToFile(); } catch { }
                UpdateHoursSavedDisplay();
            }
            catch { }
        }

        // Attempt to increment the math-run counter for a specific computed input value.
        // Returns true if the counter was incremented (i.e., this value hasn't been counted yet), false otherwise.
        private bool TryIncrementForCopiedValue(decimal value)
        {
            try
            {
                if (_lastCopiedInputValue.HasValue && _lastCopiedInputValue.Value == value)
                    return false;

                _lastCopiedInputValue = value;
                IncrementAndPersistUniqueCopyCount();
                return true;
            }
            catch
            {
                return false;
            }
        }
        // Return a single path suitable for storing the math-run count file.
        // Prefer LocalLow -> LocalApplicationData -> Roaming (ApplicationData) -> current directory.
        private string GetAppDataMathRunFilePath()
        {
            try
            {
                // Attempt LocalLow first (useful for some low-integrity app scenarios)
                string localLow = null;
                try
                {
                    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(userProfile))
                        localLow = Path.Combine(userProfile, "AppData", "LocalLow");
                }
                catch { }

                string baseDir = null;
                if (!string.IsNullOrEmpty(localLow)) baseDir = localLow;
                if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(baseDir)) baseDir = ".";

                var dir = Path.Combine(baseDir, "SEK-CALC");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "mathruns.txt");
            }
            catch
            {
                return Path.Combine(".", "mathruns.txt");
            }
        }

        // Return a prioritized list of possible math-run file locations to check/read from.
        private string[] GetAllMathRunFilePaths()
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                // LocalLow (per-user low integrity) - useful for some packaged app scenarios
                try
                {
                    var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(up))
                    {
                        var localLow = Path.Combine(up, "AppData", "LocalLow", "SEK-CALC", "mathruns.txt");
                        list.Add(localLow);
                    }
                }
                catch { }

                // Local (per-user)
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                    list.Add(Path.Combine(local, "SEK-CALC", "mathruns.txt"));
            }
            catch { }
            try
            {
                // Roaming (per-user, may roam with profile)
                var roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(roam))
                    list.Add(Path.Combine(roam, "SEK-CALC", "mathruns.txt"));
            }
            catch { }
            try
            {
                // Current directory as last resort
                list.Add(Path.Combine(Environment.CurrentDirectory, "SEK-CALC_mathruns.txt"));
            }
            catch { }
            return list.ToArray();
        }

        // Settings stored in JSON so the app can persist across reinstalls when writing to AppData
        private class AppSettings
        {
            public long UniqueCopyCount { get; set; }
            // Standard/default values requested by user
            public decimal Multiplier { get; set; } = 10.5m;
            public int RoundMode { get; set; } = 1;
            public bool AlwaysOnTop { get; set; } = true;
            // SecondsPerRun persisted so it can be adjusted by editing settings.json
            public int SecondsPerRun { get; set; } = 10;
            public int? MainWindowWidth { get; set; }
            public int? MainWindowHeight { get; set; }
            public int? MainWindowX { get; set; }
            public int? MainWindowY { get; set; }
        }

        private string GetSettingsFilePath()
        {
            try
            {
                var mathPath = GetAppDataMathRunFilePath();
                var dir = Path.GetDirectoryName(mathPath) ?? ".";
                return Path.Combine(dir, "settings.json");
            }
            catch
            {
                return Path.Combine(Environment.CurrentDirectory, "SEK-CALC_settings.json");
            }
        }

        private void SaveSettingsToFile()
        {
            try
            {
                var path = GetSettingsFilePath();
                var model = new AppSettings();
                // authoritative values come from in-memory/UI state first
                model.UniqueCopyCount = _uniqueCopyCount;
                // Multiplier from UI if available
                try
                {
                    if (SettingsMultiplierBox != null && !string.IsNullOrWhiteSpace(SettingsMultiplierBox.Text) && decimal.TryParse(SettingsMultiplierBox.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal mm))
                        model.Multiplier = mm;
                    else
                    {
                        var local = ApplicationData.Current.LocalSettings;
                        if (local != null && local.Values.TryGetValue("Multiplier", out object mobj) && decimal.TryParse(mobj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal mv))
                            model.Multiplier = mv;
                    }
                }
                catch { }

                // RoundMode & AlwaysOnTop from UI if present
                try
                {
                    if (SettingsRoundUpCheck != null)
                        model.RoundMode = SettingsRoundUpCheck.IsChecked == true ? 1 : 0;
                    else
                    {
                        var local = ApplicationData.Current.LocalSettings;
                        if (local != null && local.Values.TryGetValue("RoundMode", out object r))
                            try { model.RoundMode = Convert.ToInt32(r); } catch { }
                    }

                    if (SettingsAlwaysOnTopCheck != null)
                        model.AlwaysOnTop = SettingsAlwaysOnTopCheck.IsChecked == true;
                    else
                    {
                        var local = ApplicationData.Current.LocalSettings;
                        if (local != null && local.Values.TryGetValue("AlwaysOnTop", out object t))
                            try { model.AlwaysOnTop = Convert.ToBoolean(t); } catch { }
                    }
                }
                catch { }

                // SecondsPerRun persisted from in-memory value
                try
                {
                    model.SecondsPerRun = _secondsPerRun;
                }
                catch { }

                // Window size/pos: prefer current AppWindow values
                try
                {
                    var appWin = GetAppWindow();
                    if (appWin != null)
                    {
                        model.MainWindowWidth = appWin.Size.Width;
                        model.MainWindowHeight = appWin.Size.Height;
                        try { var p = appWin.Position; model.MainWindowX = p.X; model.MainWindowY = p.Y; } catch { }
                    }
                }
                catch { }

                var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                // Avoid overwriting if file already contains the same content
                try
                {
                    if (File.Exists(path))
                    {
                        var existing = File.ReadAllText(path);
                        if (string.Equals(existing, json, StringComparison.Ordinal))
                        {
                            _persistenceDiag = "Settings up-to-date: " + path;
                            return;
                        }
                    }
                }
                catch { }

                File.WriteAllText(path, json);
                _lastSettingsWriteUtc = DateTime.UtcNow;
                _persistenceDiag = "Saved settings to: " + path;
                // Update LocalSettings to reflect persisted JSON so app components reading LocalSettings remain in sync
                try
                {
                    var local = ApplicationData.Current.LocalSettings;
                    // Sync the unique copy count into LocalSettings as the authoritative run counter
                    local.Values["UniqueCopyCount"] = model.UniqueCopyCount.ToString(CultureInfo.InvariantCulture);
                    // Keep legacy MathRunCount key for compatibility
                    local.Values["MathRunCount"] = model.UniqueCopyCount.ToString(CultureInfo.InvariantCulture);
                    local.Values["Multiplier"] = model.Multiplier.ToString(CultureInfo.InvariantCulture);
                    local.Values["RoundMode"] = model.RoundMode;
                    local.Values["AlwaysOnTop"] = model.AlwaysOnTop;
                    // Persist SecondsPerRun into LocalSettings so other components can read it if needed
                    local.Values["SecondsPerRun"] = model.SecondsPerRun.ToString(CultureInfo.InvariantCulture);
                    if (model.MainWindowWidth.HasValue) local.Values["MainWindowWidth"] = model.MainWindowWidth.Value.ToString(CultureInfo.InvariantCulture);
                    if (model.MainWindowHeight.HasValue) local.Values["MainWindowHeight"] = model.MainWindowHeight.Value.ToString(CultureInfo.InvariantCulture);
                    if (model.MainWindowX.HasValue) local.Values["MainWindowX"] = model.MainWindowX.Value.ToString(CultureInfo.InvariantCulture);
                    if (model.MainWindowY.HasValue) local.Values["MainWindowY"] = model.MainWindowY.Value.ToString(CultureInfo.InvariantCulture);
                }
                catch { }
            }
            catch (Exception ex)
            {
                _persistenceDiag = "SaveSettings failed: " + ex.Message;
            }
        }

        private AppSettings? LoadSettingsFromFile()
        {
            try
            {
                var path = GetSettingsFilePath();
                if (!File.Exists(path))
                {
                    _persistenceDiag = "Settings file missing: " + path;
                    return null;
                }
                var json = File.ReadAllText(path);
                var model = JsonSerializer.Deserialize<AppSettings>(json);
                _persistenceDiag = "Loaded settings from: " + path;
                // initialize file watcher after successful load
                try { InitSettingsFileWatcher(path); } catch { }
                return model;
            }
            catch (Exception ex)
            {
                _persistenceDiag = "LoadSettings failed: " + ex.Message;
            }
            return null;
        }

        private void InitSettingsFileWatcher(string settingsPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(settingsPath);
                var file = Path.GetFileName(settingsPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
                    return;

                // Dispose existing watcher
                try { _settingsWatcher?.Dispose(); } catch { }

                var watcher = new FileSystemWatcher(dir, file);
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                watcher.Changed += OnSettingsFileChanged;
                watcher.Created += OnSettingsFileChanged;
                watcher.Renamed += OnSettingsFileChanged;
                watcher.EnableRaisingEvents = true;
                _settingsWatcher = watcher;
            }
            catch { }
        }

        private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Ignore changes we wrote ourselves recently
                var now = DateTime.UtcNow;
                if ((now - _lastSettingsWriteUtc).TotalMilliseconds < 1000)
                    return;

                // Small delay to allow write to complete
                Task.Delay(200).ContinueWith(t =>
                {
                    try
                    {
                        var settings = LoadSettingsFromFile();
                        if (settings != null)
                        {
                            // apply loaded settings on UI thread
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                try
                                {
                                _uniqueCopyCount = settings.UniqueCopyCount;
                                    // Apply SecondsPerRun from settings.json to in-memory value
                                    try { _secondsPerRun = settings.SecondsPerRun; } catch { }
                                    if (SettingsMultiplierBox != null) SettingsMultiplierBox.Text = settings.Multiplier.ToString(CultureInfo.InvariantCulture);
                                    if (SettingsRoundUpCheck != null) SettingsRoundUpCheck.IsChecked = settings.RoundMode == 1;
                                    if (SettingsAlwaysOnTopCheck != null) SettingsAlwaysOnTopCheck.IsChecked = settings.AlwaysOnTop;
                                    try { SetWindowTopmost(settings.AlwaysOnTop); } catch { }
                                    UpdateHoursSavedDisplay();
                                }
                                catch { }
                            });
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        public MainWindow()
        {
            InitializeComponent();
            // Synchronously load settings.json at startup and apply values immediately
            try
            {
                var loaded = LoadSettingsFromFile();
                if (loaded != null)
                {
                    try
                    {
                        // Load unique copy count from settings.json and keep in-memory counter in sync.
                        try { _uniqueCopyCount = loaded.UniqueCopyCount; } catch { }
                        // Apply SecondsPerRun from settings.json to in-memory value
                        try { _secondsPerRun = loaded.SecondsPerRun; } catch { }

                        if (SettingsMultiplierBox != null)
                            SettingsMultiplierBox.Text = loaded.Multiplier.ToString(CultureInfo.InvariantCulture);
                        if (SettingsRoundUpCheck != null)
                            SettingsRoundUpCheck.IsChecked = loaded.RoundMode == 1;
                        if (SettingsAlwaysOnTopCheck != null)
                            SettingsAlwaysOnTopCheck.IsChecked = loaded.AlwaysOnTop;
                        try { SetWindowTopmost(loaded.AlwaysOnTop); } catch { }

                        // Apply window size/position if present
                        try
                        {
                            var appWindow = GetAppWindow();
                            if (appWindow != null)
                            {
                                if (loaded.MainWindowWidth.HasValue && loaded.MainWindowHeight.HasValue)
                                {
                                    appWindow.Resize(new SizeInt32(loaded.MainWindowWidth.Value, loaded.MainWindowHeight.Value));
                                }
                                if (loaded.MainWindowX.HasValue && loaded.MainWindowY.HasValue)
                                {
                                    try { appWindow.Move(new PointInt32(loaded.MainWindowX.Value, loaded.MainWindowY.Value)); } catch { }
                                }
                            }
                        }
                        catch { }

                        // If width/height missing in settings, do not override program defaults here.
                        // The program's default window size is controlled elsewhere; avoid forcing 770x550.

                        // Keep LocalSettings in sync so other code reading LocalSettings is consistent
                        try
                        {
                            var local = ApplicationData.Current.LocalSettings;
                            // Sync unique copy count so UI and other components can read it from LocalSettings
                            local.Values["UniqueCopyCount"] = _uniqueCopyCount.ToString(CultureInfo.InvariantCulture);
                            local.Values["Multiplier"] = loaded.Multiplier.ToString(CultureInfo.InvariantCulture);
                            local.Values["RoundMode"] = loaded.RoundMode;
                            local.Values["AlwaysOnTop"] = loaded.AlwaysOnTop;
                // Persist SecondsPerRun into LocalSettings so other components can read it if needed
                if (loaded != null)
                {
                    try { local.Values["SecondsPerRun"] = loaded.SecondsPerRun.ToString(CultureInfo.InvariantCulture); } catch { }
                }
                            if (loaded.MainWindowWidth.HasValue) local.Values["MainWindowWidth"] = loaded.MainWindowWidth.Value.ToString(CultureInfo.InvariantCulture);
                            if (loaded.MainWindowHeight.HasValue) local.Values["MainWindowHeight"] = loaded.MainWindowHeight.Value.ToString(CultureInfo.InvariantCulture);
                            if (loaded.MainWindowX.HasValue) local.Values["MainWindowX"] = loaded.MainWindowX.Value.ToString(CultureInfo.InvariantCulture);
                            if (loaded.MainWindowY.HasValue) local.Values["MainWindowY"] = loaded.MainWindowY.Value.ToString(CultureInfo.InvariantCulture);
                        }
                        catch { }

                        UpdateHoursSavedDisplay();
                    }
                    catch { }
                }
                else
                {
                    // First-run (no settings.json): apply default window size and persist explicit default settings
                    try
                    {
                        var appWindow = GetAppWindow();
                        if (appWindow != null)
                        {
                            appWindow.Resize(new SizeInt32(770, 550));
                        }
                    }
                    catch { }

                    try
                    {
                        var model = new AppSettings()
                        {
                            UniqueCopyCount = _uniqueCopyCount,
                            Multiplier = 10.5m,
                            RoundMode = 1,
                            AlwaysOnTop = true,
                            SecondsPerRun = _secondsPerRun,
                            MainWindowWidth = 770,
                            MainWindowHeight = 550
                        };

                        var path = GetSettingsFilePath();
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir))
                            try { Directory.CreateDirectory(dir); } catch { }

                        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(path, json);
                        _lastSettingsWriteUtc = DateTime.UtcNow;
                        _persistenceDiag = "Saved default settings to: " + path;

                        // Init watcher for the new settings file
                        try { InitSettingsFileWatcher(path); } catch { }

                        // Sync LocalSettings
                        try
                        {
                            var local = ApplicationData.Current.LocalSettings;
                            local.Values["UniqueCopyCount"] = model.UniqueCopyCount.ToString(CultureInfo.InvariantCulture);
                            local.Values["MathRunCount"] = model.UniqueCopyCount.ToString(CultureInfo.InvariantCulture);
                            local.Values["Multiplier"] = model.Multiplier.ToString(CultureInfo.InvariantCulture);
                            local.Values["RoundMode"] = model.RoundMode;
                            local.Values["AlwaysOnTop"] = model.AlwaysOnTop;
                            local.Values["SecondsPerRun"] = model.SecondsPerRun.ToString(CultureInfo.InvariantCulture);
                            local.Values["MainWindowWidth"] = model.MainWindowWidth.Value.ToString(CultureInfo.InvariantCulture);
                            local.Values["MainWindowHeight"] = model.MainWindowHeight.Value.ToString(CultureInfo.InvariantCulture);
                        }
                        catch { }

                        // Update UI state to reflect defaults
                        try
                        {
                            if (SettingsMultiplierBox != null) SettingsMultiplierBox.Text = model.Multiplier.ToString(CultureInfo.InvariantCulture);
                            if (SettingsRoundUpCheck != null) SettingsRoundUpCheck.IsChecked = model.RoundMode == 1;
                            if (SettingsAlwaysOnTopCheck != null) SettingsAlwaysOnTopCheck.IsChecked = model.AlwaysOnTop;
                            try { SetWindowTopmost(model.AlwaysOnTop); } catch { }
                        }
                        catch { }
                    }
                    catch { }
                }
            }
            catch { }
            // (removed: one-time clear of LocalSettings) - preserve user settings across restarts
            // Default input to 1 on start, then compute immediately
            try
            {
                InputBox!.Text = "1";
                ComputeAndShow();
            }
            catch
            {
                // ignore if InputBox not ready
            }
            // Check clipboard when the app starts (may override the default if clipboard contains a number)
            _ = CheckClipboardAndApplyAsync();
            // Also check clipboard when window is activated (clicked)
            this.Activated += OnWindowActivated;
            // Save on window close as an extra safeguard
            this.Closed += (s, e) => { try { SaveSettingsToFile(); } catch { } };

            // Set settings panel version text
            try
            {
                string versionText = "v0.0.0";
                var entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                string rawForTooltip = null;
                if (entry != null)
                {
                    try
                    {
                    // Prefer MSIX package version when available (shows full 4-part version as in Windows App settings)
                    try
                    {
                        var pkg = Windows.ApplicationModel.Package.Current;
                        if (pkg != null)
                        {
                            var ver = pkg.Id.Version;
                            versionText = $"v{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";
                            rawForTooltip = versionText;
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(versionText) || versionText == "v0.0.0")
                    {
                        // Prefer AssemblyInformationalVersion which is often populated by Git versioning tools
                        string raw = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                        if (string.IsNullOrEmpty(raw))
                        {
                            // Fall back to File/Product version or assembly version
                            if (!string.IsNullOrEmpty(entry.Location))
                            {
                                var fv = FileVersionInfo.GetVersionInfo(entry.Location);
                                raw = !string.IsNullOrEmpty(fv.ProductVersion) ? fv.ProductVersion : entry.GetName().Version?.ToString();
                            }
                            else
                            {
                                raw = entry.GetName().Version?.ToString();
                            }
                        }

                        if (!string.IsNullOrEmpty(raw))
                        {
                            // Preserve raw for tooltip
                            rawForTooltip = raw;
                            // Strip build metadata and prerelease suffix (after '+' or '-')
                            var core = raw.Split(new char[] { '+', '-' }, 2)[0];
                            // If version has more than 3 components, keep first 3 (major.minor.patch)
                            var parts = core.Split('.');
                            if (parts.Length >= 3)
                            {
                                versionText = "v" + string.Join('.', parts[0], parts[1], parts[2]);
                            }
                            else if (parts.Length == 2)
                            {
                                versionText = "v" + string.Join('.', parts[0], parts[1], "0");
                            }
                            else
                            {
                                versionText = "v" + core;
                            }
                        }
                    }
                    }
                    catch { }
                }
                try { SettingsVersionText.Text = versionText; } catch { }
                try { if (!string.IsNullOrEmpty(rawForTooltip)) Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(SettingsVersionText, rawForTooltip); } catch { }
            }
            catch { }

            // Restore last window size if available and subscribe to changes
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                var appWindow = GetAppWindow();
                if (appWindow != null)
                {
                    // Try LocalSettings first, then RoamingSettings for previously-synced size.
                    bool restored = false;
                    if (localSettings.Values.TryGetValue("MainWindowWidth", out object wobj) &&
                        int.TryParse(wobj as string, NumberStyles.Any, CultureInfo.InvariantCulture, out int wi) &&
                        localSettings.Values.TryGetValue("MainWindowHeight", out object hobj) &&
                        int.TryParse(hobj as string, NumberStyles.Any, CultureInfo.InvariantCulture, out int hi))
                    {
                        appWindow.Resize(new SizeInt32(wi, hi));
                        restored = true;
                    }

                    else
                    {
                        try
                        {
                            var roaming = ApplicationData.Current.RoamingSettings;
                            if (roaming.Values.TryGetValue("MainWindowWidth", out object rwobj) &&
                                int.TryParse(rwobj as string, NumberStyles.Any, CultureInfo.InvariantCulture, out int rwi) &&
                                roaming.Values.TryGetValue("MainWindowHeight", out object rhobj) &&
                                int.TryParse(rhobj as string, NumberStyles.Any, CultureInfo.InvariantCulture, out int rhi))
                            {
                                appWindow.Resize(new SizeInt32(rwi, rhi));
                                restored = true;
                            }
                        }
                        catch
                        {
                            // ignore roaming access errors
                        }
                    }

                    if (!restored)
                    {
                        // reasonable default size if none saved
                        appWindow.Resize(new SizeInt32(800, 450));
                    }

                    // Try to restore window position if previously saved
                    try
                    {
                        var localPos = ApplicationData.Current.LocalSettings;
                        if (localPos.Values.TryGetValue("MainWindowX", out object xobj) && int.TryParse(xobj as string, NumberStyles.Any, CultureInfo.InvariantCulture, out int px) &&
                            localPos.Values.TryGetValue("MainWindowY", out object yobj) && int.TryParse(yobj as string, NumberStyles.Any, CultureInfo.InvariantCulture, out int py))
                        {
                            try { appWindow.Move(new PointInt32(px, py)); }
                            catch { }
                        }
                        else
                        {
                            // try roaming fallback
                            try
                            {
                                var roaming = ApplicationData.Current.RoamingSettings;
                                if (roaming.Values.TryGetValue("MainWindowX", out object rxobj) && int.TryParse(rxobj as string, NumberStyles.Any, CultureInfo.InvariantCulture, out int rpx) &&
                                    roaming.Values.TryGetValue("MainWindowY", out object ryobj) && int.TryParse(ryobj as string, NumberStyles.Any, CultureInfo.InvariantCulture, out int rpy))
                                {
                                    try { appWindow.Move(new PointInt32(rpx, rpy)); } catch { }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    appWindow.Changed += OnAppWindowChanged;
                    // Use title bar extension so top bar matches window
                    try
                    {
                        this.ExtendsContentIntoTitleBar = true;
                        // make DragArea the draggable title bar region (DragArea spans full width so edges remain draggable)
                        this.SetTitleBar(DragArea!);
                // set native window icons so taskbar and titlebar previews use the custom icon
                try
                {
                SetWindowIconFromFile(Path.Combine(AppContext.BaseDirectory, "dollar-sign.ico"));
                    // Restore settings hint (multiplier) so UI shows correct multiplier
                    try
                    {
                        if (localSettings.Values.TryGetValue("Multiplier", out object multObj))
                        {
                            double m;
                            if (double.TryParse(multObj.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out m))
                            {
                                HintText!.Text = $"Result is input × {m.ToString(CultureInfo.InvariantCulture).Replace('.', ',')}";
                            }
                        }
                    }
                    catch { }

                    // Restore saved settings (multiplier and checkboxes) for UI so app remembers user choices
                    try
                    {
                        var local = ApplicationData.Current.LocalSettings;
                        if (local != null)
                        {
                            // Multiplier: set settings box text if present
                            try
                            {
                                if (local.Values.TryGetValue("Multiplier", out object mobj))
                                {
                                    var ms = mobj?.ToString() ?? "";
                                    if (!string.IsNullOrWhiteSpace(ms) && SettingsMultiplierBox != null)
                                        SettingsMultiplierBox.Text = ms;
                                }
                            }
                            catch { }

                        // SecondsPerRun: restore from LocalSettings if present (fallback), UI does not expose direct input
                        try
                        {
                            if (local.Values.TryGetValue("SecondsPerRun", out object spr))
                            {
                                if (int.TryParse(spr?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out int sVal))
                                {
                                    _secondsPerRun = sVal;
                                }
                            }
                        }
                        catch { }

                            // RoundMode: 1 = always up, 0 = normal
                            try
                            {
                                if (local.Values.TryGetValue("RoundMode", out object r))
                                {
                                    var isUp = false;
                                    try { isUp = Convert.ToInt32(r) == 1; } catch { }
                                    if (SettingsRoundUpCheck != null) SettingsRoundUpCheck.IsChecked = isUp;
                                }
                            }
                            catch { }

                            // AlwaysOnTop
                            try
                            {
                                if (local.Values.TryGetValue("AlwaysOnTop", out object t))
                                {
                                    var atop = false;
                                    try { atop = Convert.ToBoolean(t); } catch { }
                                    if (SettingsAlwaysOnTopCheck != null) SettingsAlwaysOnTopCheck.IsChecked = atop;
                                    try { SetWindowTopmost(atop); } catch { }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                }
                catch
                {
                    // ignore if icon can't be set
                }
                    }
                    catch
                    {
                        // ignore if APIs are not present
                    }
                }
            }
            catch
            {
                // ignore
            }
            // Load persisted math run count and update the display
            try
            {
                var local = ApplicationData.Current.LocalSettings;
                if (local != null && local.Values.TryGetValue("UniqueCopyCount", out object mcobj))
                {
                    if (long.TryParse(mcobj as string, NumberStyles.Any, CultureInfo.InvariantCulture, out long c))
                        _uniqueCopyCount = c;
                }
                // Additionally try to load from a file or custom folder so the count survives uninstall/reinstall.
                try
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var settings = LoadSettingsFromFile();
                            if (settings != null)
                            {
                                // apply loaded settings
                                try { _uniqueCopyCount = settings.UniqueCopyCount; } catch { }
                                try
                                {
                                    // update LocalSettings to keep parity
                                    var local = ApplicationData.Current.LocalSettings;
                                    // store under both legacy and new keys for compatibility
                                    local.Values["UniqueCopyCount"] = settings.UniqueCopyCount.ToString(CultureInfo.InvariantCulture);
                                    local.Values["MathRunCount"] = settings.UniqueCopyCount.ToString(CultureInfo.InvariantCulture);
                                    local.Values["Multiplier"] = settings.Multiplier.ToString(CultureInfo.InvariantCulture);
                                    local.Values["RoundMode"] = settings.RoundMode;
                                    local.Values["AlwaysOnTop"] = settings.AlwaysOnTop;
                                    if (settings.MainWindowWidth.HasValue) local.Values["MainWindowWidth"] = settings.MainWindowWidth.Value.ToString(CultureInfo.InvariantCulture);
                                    if (settings.MainWindowHeight.HasValue) local.Values["MainWindowHeight"] = settings.MainWindowHeight.Value.ToString(CultureInfo.InvariantCulture);
                                    if (settings.MainWindowX.HasValue) local.Values["MainWindowX"] = settings.MainWindowX.Value.ToString(CultureInfo.InvariantCulture);
                                    if (settings.MainWindowY.HasValue) local.Values["MainWindowY"] = settings.MainWindowY.Value.ToString(CultureInfo.InvariantCulture);
                                }
                                catch { }

                                // apply UI changes on UI thread
                                try { _ = DispatcherQueue.TryEnqueue(() => {
                                    try
                                    {
                                        if (SettingsMultiplierBox != null) SettingsMultiplierBox.Text = settings.Multiplier.ToString(CultureInfo.InvariantCulture);
                                        if (SettingsRoundUpCheck != null) SettingsRoundUpCheck.IsChecked = settings.RoundMode == 1;
                                        if (SettingsAlwaysOnTopCheck != null) SettingsAlwaysOnTopCheck.IsChecked = settings.AlwaysOnTop;
                                        try { SetWindowTopmost(settings.AlwaysOnTop); } catch { }
                                    }
                                    catch { }
                                }); } catch { }

                                // restore window size/position if provided
                                try
                                {
                                    var appWindow = GetAppWindow();
                                    if (appWindow != null && (settings.MainWindowWidth.HasValue || settings.MainWindowHeight.HasValue))
                                    {
                                        var w = settings.MainWindowWidth ?? appWindow.Size.Width;
                                        var h = settings.MainWindowHeight ?? appWindow.Size.Height;
                                        appWindow.Resize(new SizeInt32(w, h));
                                    }
                                    if (appWindow != null && (settings.MainWindowX.HasValue || settings.MainWindowY.HasValue))
                                    {
                                        var x = settings.MainWindowX ?? appWindow.Position.X;
                                        var y = settings.MainWindowY ?? appWindow.Position.Y;
                                        try { appWindow.Move(new PointInt32(x, y)); } catch { }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }

                        try { _ = DispatcherQueue.TryEnqueue(() => UpdateHoursSavedDisplay()); } catch { }
                    });
                }
                catch { }

                // Update display immediately with value from LocalSettings; background task may update it later.
                UpdateHoursSavedDisplay();
            }
            catch { }
        }

        private void OnInputChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressInputTextChanged)
            {
                // clear the flag and skip the automatic compute; caller will compute explicitly
                _suppressInputTextChanged = false;
                return;
            }
            ComputeAndShow();
        }

        private void OnInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                ComputeAndShow();
            }
        }

        public void ComputeAndShow()
        {
            var txt = InputBox.Text?.Trim() ?? string.Empty;
            // Try to extract a number from the text
            var match = Regex.Match(txt, "-?\\d[\\d,\\.]*");
            var candidate = match.Success ? match.Value : txt;
            // Normalize separators so comma and dot are treated the same
            var normalized = NormalizeNumberString(candidate);

            if (decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal value) ||
                decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
            {
                // Read multiplier and rounding settings from LocalSettings (persisted)
                decimal multiplier = 10.25m;
                int roundMode = 0; // 0 = normal, 1 = always round up
                try
                {
                    var local = ApplicationData.Current.LocalSettings;
                    if (local != null)
                    {
                        if (local.Values.TryGetValue("Multiplier", out object mobj))
                        {
                            if (decimal.TryParse(mobj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal mval))
                                multiplier = mval;
                        }

                        if (local.Values.TryGetValue("RoundMode", out object robj))
                        {
                            try
                            {
                                roundMode = Convert.ToInt32(robj);
                            }
                            catch { }
                        }
                        // Prefer the live UI toggle if present (reflects immediate user choice even before saving)
                        try
                        {
                            if (SettingsRoundUpCheck != null)
                            {
                                roundMode = SettingsRoundUpCheck.IsChecked == true ? 1 : 0;
                            }
                        }
                        catch { }
                    }
                }
                catch
                {
                    // ignore settings read errors and use defaults
                }

                decimal rawResult;
                // Pre-check multiplication to avoid decimal overflow
                try
                {
                    if (multiplier != 0m)
                    {
                        decimal absValue = Math.Abs(value);
                        decimal absMultiplier = Math.Abs(multiplier);
                        if (absValue > decimal.MaxValue / absMultiplier)
                        {
                            // Out of range result -> show friendly message and skip calculation
                            ResultText.Text = "Error: result out of range";
                            return;
                        }
                    }
                }
                catch
                {
                    // In case of any unexpected error during check, avoid throwing from UI path
                    ResultText.Text = "Error: calculation unavailable";
                    return;
                }

                rawResult = value * multiplier;
                decimal result;
                if (roundMode == 1)
                {
                    // Always round up to 2 decimal places (overflow-safe)
                    result = CeilingTo2Decimals(rawResult);
                }
                else
                {
                    // Standard rounding to 2 decimals (banker's rounding)
                    result = Math.Round(rawResult, 2, MidpointRounding.ToEven);
                }

                // Format without thousands separator and with comma as decimal separator (e.g. 59000,50)
                var formatted = result.ToString("F2", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
                ResultText!.Text = formatted + " SEK";

                // Remember last computed input value (we no longer count math runs here)
                try
                {
                    _lastComputedInputValue = value;
                    // New computation -> clear last-copied marker so future copies count for this new math run
                    _lastCopiedInputValue = null;
                }
                catch { }
            }
            else
            {
                // If input looks numeric but couldn't be parsed (too large), show range error;
                // otherwise clear the result for non-numeric input.
                if (Regex.IsMatch(normalized, @"^-?\d+([.,]\d+)?$"))
                    ResultText.Text = "Error: result out of range";
                else
                    ResultText.Text = string.Empty;
            }
        }

        // Overflow-safe ceiling to 2 decimal places. Scales only the fractional part so
        // very large integer parts do not cause decimal overflow when multiplying by 100.
        private static decimal CeilingTo2Decimals(decimal value)
        {
            if (value == decimal.Zero)
                return decimal.Zero;
            bool negative = value < 0m;
            decimal abs = negative ? -value : value;

            decimal intPart = decimal.Truncate(abs);
            decimal frac = abs - intPart;

            // Scale only the fractional part (frac < 1 so frac*100 < 100)
            decimal scaledFrac = decimal.Truncate(frac * 100m);

            // If there's any remainder beyond the truncated part, increment for ceiling
            if ((frac * 100m) != scaledFrac)
                scaledFrac += 1m;

            decimal resultAbs = intPart + (scaledFrac / 100m);
            return negative ? -resultAbs : resultAbs;
        }

        private async void ResultText_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // Prevent duplicate handling (e.g., pointer + tap events firing)
            if (_isHandlingCopy)
                return;
            _isHandlingCopy = true;
            try
            {
                // First try to read the current clipboard and use it as input if it contains a number.
                var dp = Clipboard.GetContent();
                if (dp != null && dp.Contains(StandardDataFormats.Text))
                {
                    var text = await dp.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // If the clipboard already contains the value we last placed there, treat this as a simple copy confirmation
                        if (!string.IsNullOrEmpty(_lastClipboardTextApplied) && string.Equals(_lastClipboardTextApplied, text, StringComparison.Ordinal))
                        {
                            // Re-copy the currently displayed result (ensures clipboard is the computed value) and show confirmation
                            var curText2 = ResultText!.Text ?? string.Empty;
                            var curNum2 = curText2.Replace("SEK", "").Trim();
                            var dp2 = new DataPackage();
                            dp2.SetText(curNum2);
                            Clipboard.SetContent(dp2);
                            try { Clipboard.Flush(); } catch { }
                            _lastClipboardTextApplied = curNum2;
                            // Count this copy action, but avoid double-counting the same computed value
                    try
                    {
                        var norm = NormalizeNumberString(curNum2);
                        if (decimal.TryParse(norm, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed))
                        {
                            TryIncrementForCopiedValue(parsed);
                        }
                        else
                        {
                            IncrementAndPersistUniqueCopyCount();
                        }
                    }
                    catch { IncrementAndPersistUniqueCopyCount(); }
                            _ = ShowCopyConfirmationAsync();
                            return;
                        }
                        var match = Regex.Match(text, "-?\\d[\\d,\\.]*");
                        if (match.Success)
                        {
                            var candidate = match.Value;
                            var normalized = NormalizeNumberString(candidate);
                            if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double value) ||
                                double.TryParse(normalized, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
                            {
                                // Ignore very large numbers when applying from the clipboard here as well.
                                // Only manual input (typing and pressing Enter) should allow very large values.
                                bool allowAutoApply = true;
                                try
                                {
                                    if (Math.Abs(value) > 9999.0)
                                        allowAutoApply = false;
                                }
                                catch { }

                                if (!allowAutoApply)
                                {
                                    // Skip trying to apply this clipboard value; fall through to fallback copy of current result.
                                }
                                else
                                {
                                // If the InputBox already contains the same numeric value, avoid re-applying and recomputing
                                try
                                {
                                    var curInput = InputBox?.Text ?? string.Empty;
                                    var curNorm = NormalizeNumberString(curInput);
                                    if (double.TryParse(curNorm, NumberStyles.Any, CultureInfo.InvariantCulture, out double curVal) ||
                                        double.TryParse(curNorm, NumberStyles.Any, CultureInfo.CurrentCulture, out curVal))
                                    {
                                        if (Math.Abs(curVal - value) < 1e-12)
                                        {
                                            // Input already matches clipboard numeric value; just copy the currently displayed result
                                            var existingResult = ResultText!.Text ?? string.Empty;
                                            var existingNum = existingResult.Replace("SEK", "").Trim();
                                            var dataPackage2 = new DataPackage();
                                            dataPackage2.SetText(existingNum);
                                            Clipboard.SetContent(dataPackage2);
                                            try { Clipboard.Flush(); } catch { }
                                            _lastClipboardTextApplied = existingNum;
                                            // Count this copy action, but avoid double-counting the same computed value
                                            try
                                            {
                                                var norm2 = NormalizeNumberString(existingNum);
                                                if (decimal.TryParse(norm2, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed2))
                                                {
                                                    TryIncrementForCopiedValue(parsed2);
                                                }
                                                else
                                                {
                                                    IncrementAndPersistUniqueCopyCount();
                                                }
                                            }
                                            catch { IncrementAndPersistUniqueCopyCount(); }
                                            _ = ShowCopyConfirmationAsync();
                                            return;
                                        }
                                    }
                                }
                                catch { }

                                // Update input from clipboard and recompute (avoid double-compute by suppressing TextChanged)
                                var display = normalized.Replace(".", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
                                _suppressInputTextChanged = true;
                                InputBox!.Text = display;
                                // compute once explicitly
                                ComputeAndShow();

                                // Copy the numeric part of the freshly computed result into the clipboard
                                var resultText = ResultText!.Text ?? string.Empty;
                                var num = resultText.Replace("SEK", "").Trim();
                                var dataPackage = new DataPackage();
                                dataPackage.SetText(num);
                                Clipboard.SetContent(dataPackage);
                                try { Clipboard.Flush(); } catch { }

                                // remember we applied the result we just placed into the clipboard so follow-up clipboard checks won't reapply
                                _lastClipboardTextApplied = num;
                                // Count this copy action, but avoid double-counting the same computed value
                                try
                                {
                                    var norm3 = NormalizeNumberString(num);
                                    if (decimal.TryParse(norm3, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed3))
                                    {
                                        TryIncrementForCopiedValue(parsed3);
                                    }
                                                else
                                                {
                                                    IncrementAndPersistUniqueCopyCount();
                                                }
                                }
                                            catch { IncrementAndPersistUniqueCopyCount(); }
                                _ = ShowCopyConfirmationAsync();
                                return;
                                }
                            }
                        }
                    }
                }

                // Fallback: copy current displayed numeric result
                var curText = ResultText!.Text ?? string.Empty;
                var curNum = curText.Replace("SEK", "").Trim();
                var fallbackPackage = new DataPackage();
                fallbackPackage.SetText(curNum);
                Clipboard.SetContent(fallbackPackage);
                try { Clipboard.Flush(); } catch { }
                _lastClipboardTextApplied = curNum;
                // Count this copy action, but avoid double-counting the same computed value
                try
                {
                    var norm4 = NormalizeNumberString(curNum);
                    if (decimal.TryParse(norm4, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed4))
                    {
                        TryIncrementForCopiedValue(parsed4);
                    }
                    else
                    {
                        IncrementAndPersistUniqueCopyCount();
                    }
                }
                catch { IncrementAndPersistUniqueCopyCount(); }
                _ = ShowCopyConfirmationAsync();
            }
            catch
            {
                // ignore any clipboard/access errors
            }
            finally
            {
                _isHandlingCopy = false;
            }
        }

        // Expand clickable area: treat pointer presses inside an expanded vertical area around the ResultText as taps.
        private void ResultBorder_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (ResultBorder == null || ResultText == null)
                return;

            try
            {
                var pt = e.GetCurrentPoint(ResultBorder!).Position;
                var transform = ResultText!.TransformToVisual(ResultBorder!);
                var rect = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, ResultText!.ActualWidth, ResultText!.ActualHeight));

                double expand = rect.Height * 1.5;
                var expanded = new Windows.Foundation.Rect(rect.X, rect.Y - expand, rect.Width, rect.Height + (expand * 2));

                if (expanded.Contains(pt))
                {
                    // Treat as a tap
                    ResultText_Tapped(ResultText, null);
                    e.Handled = true;
                }
            }
            catch
            {
                try { ResultText_Tapped(ResultText, null); } catch { }
            }
        }

        private async void OnNameTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                var uri = new Uri("https://github.com/Jacobnordvall/SEK-CALC");
                await Launcher.LaunchUriAsync(uri);
            }
            catch
            {
                // ignore failures to launch
            }
        }

        private void OnAlwaysOnTopLabelTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // No-op: always-on-top moved to settings window
        }

        private async Task ShowCopyConfirmationAsync()
        {
            try
            {
                // Show global overlay confirmation on top of all UI inside the window
                var dq = DispatcherQueue;
                if (dq != null)
                {
                    _ = dq.TryEnqueue(() =>
                    {
                        try
                        {
                            var b = GlobalCopiedBorder;
                            if (b != null)
                            {
                                // offset popup upward closer to the output text
                                var offsetY = -10; // adjust this value (negative moves up)
                                b.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform { Y = offsetY };
                                b.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                                b.Opacity = 1;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"ShowCopyConfirmationAsync show failed: {ex}");
                        }
                    });
                }

                await Task.Delay(900);

                var dq2 = DispatcherQueue;
                if (dq2 != null)
                {
                    _ = dq2.TryEnqueue(() =>
                    {
                        try
                        {
                            var b = GlobalCopiedBorder;
                            if (b != null)
                            {
                                b.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                                b.Opacity = 0;
                                // reset transform
                                b.RenderTransform = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"ShowCopyConfirmationAsync hide failed: {ex}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShowCopyConfirmationAsync failed: {ex}");
            }
        }

        private void UpdateHoursSavedDisplay()
        {
            try
            {
                double hours = (_uniqueCopyCount * (double)_secondsPerRun) / 3600.0;
                var hoursStrDetailed = hours.ToString("F2", CultureInfo.CurrentCulture);
                var hoursStrMain = hours.ToString("F0", CultureInfo.CurrentCulture);
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        // Update main window short summary (use FindName to avoid generated-field dependency)
                        try
                        {
                            var root = this.Content as FrameworkElement;
                            var mainSaved = root?.FindName("MainSavedText") as TextBlock;
                            if (mainSaved != null)
                                mainSaved.Text = $"Saved Frikab: {hoursStrMain}h";

                            var settingsSaved = root?.FindName("SettingsHoursSavedText") as TextBlock;
                            if (settingsSaved != null)
                                settingsSaved.Text = $"Hours saved: {hoursStrDetailed} h ({_uniqueCopyCount} runs)";
                        }
                        catch { }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                var s = sender.Size;
                localSettings.Values["MainWindowWidth"] = s.Width.ToString(CultureInfo.InvariantCulture);
                localSettings.Values["MainWindowHeight"] = s.Height.ToString(CultureInfo.InvariantCulture);
                try
                {
                    var p = sender.Position;
                    localSettings.Values["MainWindowX"] = p.X.ToString(CultureInfo.InvariantCulture);
                    localSettings.Values["MainWindowY"] = p.Y.ToString(CultureInfo.InvariantCulture);
                }
                catch { }
                // Persist window position/size to JSON settings as well (saved on close or explicit save)
                // scale UI elements to better use available space
                double width = s.Width;
                double height = s.Height;

                // Result should take a large portion of height but clamp to sensible min/max so it doesn't get too big or too small.
                double resultFont = Math.Clamp(height * 0.28, 24, 72); // between 24 and 72
                double inputFont = Math.Clamp(height * 0.06, 12, 20);
                double labelFont = Math.Clamp(height * 0.05, 12, 18);

                // Apply sizes on UI thread
                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        ResultText!.FontSize = resultFont;
                        InputBox!.FontSize = inputFont;
                        UsdLabel!.FontSize = labelFont;
                        // make hint and owner use the same label font so they scale identically
                        HintText!.FontSize = labelFont;
                        if (OwnerText != null) OwnerText.FontSize = labelFont;

                        // Scale settings panel controls so they match main UI scaling in small windows
                        try
                        {
                            double controlFont = Math.Clamp(height * 0.045, 10, 16);
                            var settingsMultiplier = this.Content as FrameworkElement != null ? (this.Content as FrameworkElement).FindName("SettingsMultiplierBox") as TextBox : null;
                            var settingsRound = this.Content as FrameworkElement != null ? (this.Content as FrameworkElement).FindName("SettingsRoundUpCheck") as CheckBox : null;
                            var settingsAlways = this.Content as FrameworkElement != null ? (this.Content as FrameworkElement).FindName("SettingsAlwaysOnTopCheck") as CheckBox : null;
                            var settingsSave = this.Content as FrameworkElement != null ? (this.Content as FrameworkElement).FindName("SettingsSaveButton") as Button : null;
                            var settingsClose = this.Content as FrameworkElement != null ? (this.Content as FrameworkElement).FindName("SettingsCloseButton") as Button : null;
                            if (settingsMultiplier != null) settingsMultiplier.FontSize = controlFont;
                            if (settingsRound != null) settingsRound.FontSize = controlFont;
                            if (settingsAlways != null) settingsAlways.FontSize = controlFont;
                            if (settingsSave != null) settingsSave.FontSize = controlFont;
                            if (settingsClose != null) settingsClose.FontSize = controlFont;
                        }
                        catch { }

                        // make input width scale with window
                        double inputWidth = Math.Clamp(width * 0.4, 140, 700);
                        InputBox!.Width = inputWidth;
                    });
            }
            catch
            {
                // ignore
            }
        }

        private AppWindow GetAppWindow()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(id);
        }

        private async void OnSettingsClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                // Toggle in-window settings panel visibility and pre-fill multiplier + rounding
                var visible = SettingsPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
                if (visible)
                {
                    SettingsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
                else
                {
                    try
                    {
                        var local = ApplicationData.Current.LocalSettings;
                        if (local.Values.TryGetValue("Multiplier", out var cur))
                            SettingsMultiplierBox!.Text = cur?.ToString() ?? "10.25";
                        else
                            SettingsMultiplierBox!.Text = "10.25";

                        if (local.Values.TryGetValue("RoundMode", out var r))
                            SettingsRoundUpCheck!.IsChecked = Convert.ToInt32(r) == 1;
                        else
                            SettingsRoundUpCheck!.IsChecked = true;

                        if (local.Values.TryGetValue("AlwaysOnTop", out var t))
                            SettingsAlwaysOnTopCheck!.IsChecked = Convert.ToBoolean(t);
                        else
                            SettingsAlwaysOnTopCheck!.IsChecked = false;
                    }
                    catch { }
                    SettingsPanel!.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }
            }
            catch
            {
                // ignore
            }
        }

        private void OnSettingsCloseClicked(object sender, RoutedEventArgs e)
        {
            SettingsPanel!.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        private void OnSettingsSaveClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var local = ApplicationData.Current.LocalSettings;
                // Pre-fill multiplier if empty
                if (string.IsNullOrWhiteSpace(SettingsMultiplierBox!.Text))
                {
                    if (local.Values.TryGetValue("Multiplier", out var cur))
                        SettingsMultiplierBox.Text = cur?.ToString() ?? "10.25";
                    else
                        SettingsMultiplierBox.Text = "10.25";
                }
                else if (double.TryParse(SettingsMultiplierBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var m))
                {
                    // Store as invariant string to avoid culture-dependent ToString() later
                    local.Values["Multiplier"] = m.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                // RoundMode: 1 = always up, 0 = normal
                local.Values["RoundMode"] = SettingsRoundUpCheck!.IsChecked == true ? 1 : 0;
                local.Values["AlwaysOnTop"] = SettingsAlwaysOnTopCheck!.IsChecked == true;
                // Seconds per run
                try
                {
                    // SecondsPerRun is intentionally not saved from the UI
                }
                catch { }
                try { SetWindowTopmost(SettingsAlwaysOnTopCheck!.IsChecked == true); } catch { }
                // Recompute immediately so the displayed result reflects the saved settings
                try { ComputeAndShow(); } catch { }
                // Persist settings to JSON file so they survive reinstall
                try { SaveSettingsToFile(); } catch { }
                SettingsPanel!.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
            catch { }
        }

        // Synchronous persistence methods that write to AppData locations only.
        private void SaveMathRunCountToAppData()
        {
            var sb = new StringBuilder();
            try
            {
                var text = _uniqueCopyCount.ToString(CultureInfo.InvariantCulture);
                foreach (var path in GetAllMathRunFilePaths())
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            try { Directory.CreateDirectory(dir); } catch (Exception ex) { sb.AppendLine($"  - mkdir {dir}: {ex.Message}"); }
                        }
                        File.WriteAllText(path, text);
                        sb.AppendLine($"  - {path}: written");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  - {path}: write-error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Save all error: {ex.Message}");
            }
            _persistenceDiag = sb.ToString();
        }

        // Legacy: no longer used. Settings are stored in settings.json via SaveSettingsToFile()/LoadSettingsFromFile().
        private long? LoadMathRunCountFromAppData()
        {
            return null;
        }

        // P/Invoke to set window icon
        private const uint WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        private void SetWindowIconFromFile(string iconPath)
        {
            try
            {
                if (!File.Exists(iconPath))
                    return;

                var hwnd = WindowNative.GetWindowHandle(this);

                // Load big and small icons
                IntPtr hIconBig = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                IntPtr hIconSmall = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE | LR_DEFAULTSIZE);

                if (hIconBig != IntPtr.Zero)
                    SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_BIG), hIconBig);
                if (hIconSmall != IntPtr.Zero)
                    SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_SMALL), hIconSmall);
            }
            catch
            {
                // ignore failures
            }
        }

        // P/Invoke for always-on-top
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        private void SetWindowTopmost(bool top)
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
                SetWindowPos(hwnd, top ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            }
            catch
            {
                // ignore
            }
        }

        private void OnAlwaysOnTopToggled(object sender, RoutedEventArgs e)
        {
            // moved to settings window
        }

        private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
        {
            // Only check clipboard when window becomes active (not when deactivated)
            try
            {
                var state = e.WindowActivationState;
                // WindowActivationState.Deactivated -> don't read clipboard
                if (state != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
                {
                    _ = CheckClipboardAndApplyAsync();
                }
            }
            catch
            {
                // fallback: try reading clipboard
                _ = CheckClipboardAndApplyAsync();
            }
        }

        private async Task CheckClipboardAndApplyAsync()
        {
            try
            {
                var dp = Clipboard.GetContent();
                if (dp != null && dp.Contains(StandardDataFormats.Text))
                {
                    var text = await dp.GetTextAsync();
                    if (string.IsNullOrWhiteSpace(text))
                        return;

                    // Extract number-like substring
                    var match = Regex.Match(text, "-?\\d[\\d,\\.]*");
                    if (!match.Success)
                        return;

                    var candidate = match.Value;
                    // If we've already applied this exact clipboard text, skip to avoid re-running the same value repeatedly
                    if (!string.IsNullOrEmpty(_lastClipboardTextApplied) && string.Equals(_lastClipboardTextApplied, text, StringComparison.Ordinal))
                        return;
                    var normalized = NormalizeNumberString(candidate);

                    if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double value) ||
                        double.TryParse(normalized, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
                    {
                        // Ignore very large numbers when auto-applying from the clipboard
                        try
                        {
                            if (Math.Abs(value) > 9999.0)
                                return;
                        }
                        catch { }
                        // Update input and compute
                        // Display using current culture's decimal separator
                        var display = normalized.Replace(".", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
                        InputBox!.Text = display;
                        ComputeAndShow();
                        // remember what clipboard text we applied
                        _lastClipboardTextApplied = text;
                    }
                }
            }
            catch
            {
                // ignore clipboard access errors
            }
        }

        private string NormalizeNumberString(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // Keep only digits, dot, comma, and leading minus
            var m = Regex.Match(input.Trim(), "-?[\\d.,]+");
            var s = m.Success ? m.Value : input;

            // Replace commas with dot so both are treated the same
            s = s.Replace(',', '.');

            // If there are multiple dots, assume all but the last are thousand separators and remove them
            int lastDot = s.LastIndexOf('.');
            if (lastDot > -1)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '.' && i != lastDot)
                        continue;
                    sb.Append(s[i]);
                }
                s = sb.ToString();
            }

            // If it starts with a dot (e.g. ".5"), prepend 0
            if (s.Length > 0 && s[0] == '.')
                s = "0" + s;

            return s;
        }
    }
}
