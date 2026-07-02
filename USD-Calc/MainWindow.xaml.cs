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
        private string _lastClipboardTextApplied;

        public MainWindow()
        {
            InitializeComponent();
            // Default input to 1 on start, then compute immediately
            try
            {
                InputBox.Text = "1";
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
                        this.SetTitleBar(DragArea);
                // set native window icons so taskbar and titlebar previews use the custom icon
                try
                {
                    SetWindowIconFromFile(Path.Combine(AppContext.BaseDirectory, "dollar-sign.ico"));
                    // Restore always-on-top setting if present
                    try
                    {
                        if (localSettings.Values.TryGetValue("AlwaysOnTop", out object aotObj) &&
                            bool.TryParse(aotObj as string, out bool aot) && aot)
                        {
                            SetWindowTopmost(true);
                            // ensure toggle reflects state if control is available
                            try { AlwaysOnTopToggle.IsOn = true; } catch { }
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

            if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double value) ||
                double.TryParse(normalized, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
            {
                double result = value * 10.25;
                // Format without thousands separator and with comma as decimal separator (e.g. 59000,50)
                var formatted = result.ToString("F2", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
                ResultText.Text = formatted + " SEK";
            }
            else
            {
                ResultText.Text = string.Empty;
            }
        }

        private void ResultText_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var text = ResultText.Text ?? string.Empty;
            // copy numeric part only
            var num = text.Replace("SEK", "").Trim();
            try
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(num);
                Clipboard.SetContent(dataPackage);
                // remember we just set this clipboard text so returning focus won't reapply it
                _lastClipboardTextApplied = num;
                // show confirmation
                _ = ShowCopyConfirmationAsync();
            }
            catch
            {
                // ignore
            }
        }

        private async void OnNameTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                var uri = new Uri("https://example.com"); // placeholder link
                await Launcher.LaunchUriAsync(uri);
            }
            catch
            {
                // ignore failures to launch
            }
        }

        private void OnAlwaysOnTopLabelTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                AlwaysOnTopToggle.IsOn = !AlwaysOnTopToggle.IsOn;
            }
            catch
            {
                // ignore
            }
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
                        GlobalCopiedBorder.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
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
                        GlobalCopiedBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
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
                    ResultText.FontSize = resultFont;
                    InputBox.FontSize = inputFont;
                    UsdLabel.FontSize = labelFont;
                    // make hint and owner use the same label font so they scale identically
                    HintText.FontSize = labelFont;
                    try { OwnerText.FontSize = labelFont; } catch { }
                    try { AlwaysOnTopToggle.FontSize = labelFont; } catch { }

                    // make input width scale with window
                    double inputWidth = Math.Clamp(width * 0.4, 140, 700);
                    InputBox.Width = inputWidth;
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
            try
            {
                bool isOn = AlwaysOnTopToggle.IsOn;
                SetWindowTopmost(isOn);
                try
                {
                    var localSettings = ApplicationData.Current.LocalSettings;
                    localSettings.Values["AlwaysOnTop"] = isOn.ToString();
                }
                catch { }
            }
            catch
            {
                // ignore
            }
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
                        InputBox.Text = display;
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
