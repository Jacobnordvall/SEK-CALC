using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
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
            this.Title = "Settings";
            this.PrimaryButtonText = "Save";
            this.CloseButtonText = "Cancel";

            var panel = new StackPanel { Padding = new Thickness(12), Spacing = 12 };

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
                _local.Values["Multiplier"] = m;
            }

            _local.Values["RoundMode"] = _roundingToggle.IsOn ? 1 : 0; // 1 = always up, 0 = normal
            _local.Values["AlwaysOnTop"] = _alwaysOnTopCheck.IsChecked == true;

            try
            {
                WindowHelpers.SetWindowTopmost(_alwaysOnTopCheck.IsChecked == true);
            }
            catch { }
        }
    }
}
