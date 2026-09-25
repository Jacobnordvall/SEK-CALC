using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Reflection;
using Windows.Storage;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;

namespace USD_Calc
{
    public class SettingsDialog : ContentDialog
    {
        private ApplicationDataContainer _local = ApplicationData.Current.LocalSettings;
        private TextBox _multiplierBox;
        private ToggleSwitch _roundingToggle;
        private CheckBox _alwaysOnTopCheck;

        public SettingsDialog()
        {
            // Title: show Settings with program version on the top-right in smaller text
            string versionText = "v0.0.0";
            try
            {
                var entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                if (entry != null)
                {
                    try
                    {
                        // If app is packaged (MSIX), prefer the package version shown in Windows App settings
                        try
                        {
                            var pkg = Windows.ApplicationModel.Package.Current;
                            if (pkg != null)
                            {
                                var ver = pkg.Id.Version;
                                versionText = $"v{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";
                            }
                        }
                        catch { }

                        if (string.IsNullOrEmpty(versionText) || versionText == "v0.0.0")
                        {
                            // Prefer AssemblyInformationalVersion which may include semantic version from git tools
                            string raw = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                            if (string.IsNullOrEmpty(raw))
                            {
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
                                var core = raw.Split(new char[] { '+', '-' }, 2)[0];
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
            }
            catch
            {
                // fall back silently
            }

            var titleGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new TextBlock { Text = "Settings", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(titleText, 0);
            var verText = new TextBlock { Text = versionText, FontSize = 12, Foreground = new SolidColorBrush(Colors.Gray), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.8, Margin = new Thickness(8,0,0,0) };
            Grid.SetColumn(verText, 1);

            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(verText);

            // If we have the original product/assembly version, show it on hover as a tooltip
            try { Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(verText, versionText); } catch { }

            // Keep the dialog Title string for accessibility and default template
            this.Title = "Settings";
            this.PrimaryButtonText = "Save";
            this.CloseButtonText = "Cancel";

            var panel = new StackPanel { Padding = new Thickness(12), Spacing = 12 };

            // Add a header inside the content so the version is always visible even if the dialog title area
            // doesn't render custom UI. This shows "Settings" with version on the right.
            panel.Children.Insert(0, titleGrid);

            panel.Children.Add(new TextBlock { Text = "Multiplier", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            _multiplierBox = new TextBox { Width = 140 };
            panel.Children.Add(_multiplierBox);

            panel.Children.Add(new TextBlock { Text = "Rounding mode", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            var roundingPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            roundingPanel.Children.Add(new TextBlock { Text = "Normal" });
            _roundingToggle = new ToggleSwitch { IsOn = true }; // default always up
            roundingPanel.Children.Add(_roundingToggle);
            roundingPanel.Children.Add(new TextBlock { Text = "Always round up" });
            panel.Children.Add(roundingPanel);

            _alwaysOnTopCheck = new CheckBox { Content = "Always on top" };
            panel.Children.Add(_alwaysOnTopCheck);

            this.Content = panel;

            this.Loaded += SettingsDialog_Loaded;
            this.PrimaryButtonClick += SettingsDialog_PrimaryButtonClick;
        }

        private void SettingsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // Load settings
            if (_local.Values.TryGetValue("Multiplier", out var m))
            {
                _multiplierBox.Text = m.ToString();
            }
            else
            {
                _multiplierBox.Text = "10.25";
            }

            if (_local.Values.TryGetValue("RoundMode", out var r))
            {
                _roundingToggle.IsOn = Convert.ToInt32(r) == 1;
            }
            else
            {
                _roundingToggle.IsOn = true; // default always up
            }

            if (_local.Values.TryGetValue("AlwaysOnTop", out var t))
            {
                _alwaysOnTopCheck.IsChecked = Convert.ToBoolean(t);
            }
            else
            {
                _alwaysOnTopCheck.IsChecked = false;
            }
        }

        private void SettingsDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Save settings
            if (double.TryParse(_multiplierBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var m))
            {
                // Store as invariant string to avoid culture-dependent ToString() later
                _local.Values["Multiplier"] = m.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            _local.Values["RoundMode"] = _roundingToggle.IsOn ? 1 : 0; // 1 = always up, 0 = normal
            _local.Values["AlwaysOnTop"] = _alwaysOnTopCheck.IsChecked == true;

            try
            {
                WindowHelpers.SetWindowTopmost(_alwaysOnTopCheck.IsChecked == true);
            }
            catch { }

            // Notify main window to recompute so UI updates immediately after saving settings
            try
            {
                App.MainWindowInstance?.ComputeAndShow();
            }
            catch { }
        }
    }
}
