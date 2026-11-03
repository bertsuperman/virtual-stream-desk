using System.Linq;
using System.Windows;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.App;

/// <summary>Every change is applied and saved immediately, so the deck updates live.</summary>
public partial class SettingsWindow : Window
{
    private readonly Deck _deck;

    public SettingsWindow(Deck deck)
    {
        InitializeComponent();
        _deck = deck;

        var s = deck.Config.Settings;
        RowsSlider.Value = s.Rows;
        ColumnsSlider.Value = s.Columns;
        SizeSlider.Value = s.ButtonSize;
        SpacingSlider.Value = s.Spacing;
        TopmostBox.IsChecked = s.AlwaysOnTop;
        LabelsBox.IsChecked = s.ShowLabels;
        AutostartBox.IsChecked = s.StartWithWindows;
        UpdateHiddenInfo();

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
