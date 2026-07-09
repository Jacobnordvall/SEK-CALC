using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using System;

using System.IO;


using System.Text.RegularExpressions;
using System.Text;
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

        public MainWindow()
        {
            InitializeComponent();
            // One-time clear of saved settings to allow fresh testing.
            // Use LocalSettings as the marker so installed/read-only app folders are not required.
            try
            {
                try
                {
                    var local = ApplicationData.Current.LocalSettings;
                    if (!local.Values.ContainsKey("__settings_cleared_once"))
                    {
                        try { local.Values.Clear(); } catch { }
                        try { ApplicationData.Current.RoamingSettings.Values.Clear(); } catch { }
                        try { local.Values["__settings_cleared_once"] = true; } catch { }
                    }
                }
                catch { }
            }
            catch { }
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

        private void ComputeAndShow()
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

                decimal rawResult = value * multiplier;
                decimal result;
                if (roundMode == 1)
                {
                    // Always round up to 2 decimal places
                    result = Math.Ceiling(rawResult * 100m) / 100m;
                }
                else
                {
                    // Standard rounding to 2 decimals (banker's rounding)
                    result = Math.Round(rawResult, 2, MidpointRounding.ToEven);
                }

                // Format without thousands separator and with comma as decimal separator (e.g. 59000,50)
                var formatted = result.ToString("F2", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
                ResultText!.Text = formatted + " SEK";
            }
            else
            {
                ResultText.Text = string.Empty;
            }
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
                                _ = ShowCopyConfirmationAsync();
                                return;
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
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        GlobalCopiedBorder!.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                        GlobalCopiedBorder.Opacity = 1;
                    }
                    catch
                    {
                        // ignore
                    }
                });

                await Task.Delay(900);

                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        GlobalCopiedBorder!.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        GlobalCopiedBorder.Opacity = 0;
                    }
                    catch
                    {
                        // ignore
                    }
                });
            }
            catch
            {
                // ignore
            }
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                var s = sender.Size;
                localSettings.Values["MainWindowWidth"] = s.Width.ToString(CultureInfo.InvariantCulture);
                localSettings.Values["MainWindowHeight"] = s.Height.ToString(CultureInfo.InvariantCulture);
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
                    local.Values["Multiplier"] = m;
                }

                // RoundMode: 1 = always up, 0 = normal
                local.Values["RoundMode"] = SettingsRoundUpCheck!.IsChecked == true ? 1 : 0;
                local.Values["AlwaysOnTop"] = SettingsAlwaysOnTopCheck!.IsChecked == true;
                try { SetWindowTopmost(SettingsAlwaysOnTopCheck!.IsChecked == true); } catch { }
                SettingsPanel!.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
            catch { }
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
