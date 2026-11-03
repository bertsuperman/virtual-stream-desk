using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;

namespace VirtualStreamDeck.App;

/// <summary>Every change is applied and saved immediately, so the deck updates live.</summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0xCB, 0x6E));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x6A, 0x6A));

    private readonly Deck _deck;
    private readonly ObsClient _obs;

    internal SettingsWindow(Deck deck, ObsClient obs)
    {
        InitializeComponent();
        _deck = deck;
        _obs = obs;

        var s = deck.Config.Settings;
        RowsSlider.Value = s.Rows;
        ColumnsSlider.Value = s.Columns;
        SizeSlider.Value = s.ButtonSize;
        SpacingSlider.Value = s.Spacing;
        TopmostBox.IsChecked = s.AlwaysOnTop;
        LabelsBox.IsChecked = s.ShowLabels;
        AutostartBox.IsChecked = s.StartWithWindows;
        UpdateHiddenInfo();
        VersionText.Text = $"{AppInfo.ProductName} {AppInfo.Version}\n{deck.Store.Directory}";

        ObsHostBox.Text = s.Obs.Host;
        ObsPortBox.Text = s.Obs.Port.ToString();
        ObsPasswordBox.Password = s.Obs.Password;
        ObsHostBox.TextChanged += (_, _) => ApplyObs();
        ObsPortBox.TextChanged += (_, _) => ApplyObs();
        ObsPasswordBox.PasswordChanged += (_, _) => ApplyObs();

        // Hook events only after the initial values are set.
        RowsSlider.ValueChanged += (_, _) => Apply();
        ColumnsSlider.ValueChanged += (_, _) => Apply();
        SizeSlider.ValueChanged += (_, _) => Apply();
        SpacingSlider.ValueChanged += (_, _) => Apply();
        TopmostBox.Click += (_, _) => Apply();
        LabelsBox.Click += (_, _) => Apply();
        AutostartBox.Click += (_, _) =>
        {
            AutoStart.Set(AutostartBox.IsChecked == true);
            Apply();
        };
    }

    private void Apply()
    {
        _deck.Update(c =>
        {
            c.Settings.Rows = (int)RowsSlider.Value;
            c.Settings.Columns = (int)ColumnsSlider.Value;
            c.Settings.ButtonSize = (int)SizeSlider.Value;
            c.Settings.Spacing = (int)SpacingSlider.Value;
            c.Settings.AlwaysOnTop = TopmostBox.IsChecked == true;
            c.Settings.ShowLabels = LabelsBox.IsChecked == true;
            c.Settings.StartWithWindows = AutostartBox.IsChecked == true;
        });
        UpdateHiddenInfo();
    }

    /// <summary>OBS fields change on every keystroke, so save silently (no grid rebuild) and just reconfigure the client.</summary>
    private void ApplyObs()
    {
        ObsStatus.Text = "";
        _deck.Update(c =>
        {
            c.Settings.Obs.Host = ObsHostBox.Text;
            c.Settings.Obs.Port = int.TryParse(ObsPortBox.Text.Trim(), out var port) ? port : 4455;
            c.Settings.Obs.Password = ObsPasswordBox.Password;
        }, notify: false);
        _obs.Configure(_deck.Config.Settings.Obs);
    }

    private async void ObsTest_Click(object sender, RoutedEventArgs e)
    {
        ObsStatus.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        ObsStatus.Text = "Pripájam…";
        try
        {
            _obs.Configure(_deck.Config.Settings.Obs);
            var version = await _obs.TestAsync();
            ObsStatus.Foreground = OkBrush;
            ObsStatus.Text = $"Pripojené, OBS {version}";
        }
        catch (Exception ex)
        {
            ObsStatus.Foreground = ErrorBrush;
            ObsStatus.Text = ex.Message;
        }
    }

    private void UpdateHiddenInfo()
    {
        var config = _deck.Config;
        int hidden = config.Buttons.Count(b => !config.IsVisible(b));
        HiddenInfo.Text = hidden == 0
            ? "Tlačidlá presunieš ťahaním myšou. Pravý klik na tlačidlo ho upraví."
            : $"Mimo mriežky je {hidden} tlačidiel. Nevymazali sa – po zväčšení mriežky sa vrátia.";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ShellHelper.OpenFolder(_deck.Store.Directory);
}
