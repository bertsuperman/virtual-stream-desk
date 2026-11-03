using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;

namespace VirtualStreamDeck.App;

public partial class ButtonEditorWindow : Window
{
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x4E));

    private static readonly MediaCommand[] MediaCommands =
    {
        MediaCommand.PlayPause, MediaCommand.Next, MediaCommand.Prev, MediaCommand.Stop,
        MediaCommand.VolumeUp, MediaCommand.VolumeDown, MediaCommand.Mute,
    };

    private static readonly ObsCommand[] ObsCommands =
    {
        ObsCommand.SwitchScene, ObsCommand.ToggleRecord, ObsCommand.ToggleStream,
        ObsCommand.ToggleMute, ObsCommand.ToggleSource, ObsCommand.SaveReplay,
    };

    private readonly ObsClient _obs;
    private bool _obsLoaded;
    private bool _settingObsItems;
    private int _obsLoadVersion;

    private readonly DeckConfig _config;
    private readonly DeckButton? _existing;
    private readonly int _row, _col;
    private readonly IconService _icons;
    private string? _icon;

    internal ButtonEditorWindow(DeckConfig config, DeckButton? existing, int row, int col, IconService icons, ObsClient obs)
    {
        InitializeComponent();
        _obs = obs;
        _config = config;
        _existing = existing;
        _row = row;
        _col = col;
        _icons = icons;

        foreach (var cmd in MediaCommands)
            MediaBox.Items.Add(new ComboBoxItem { Content = MainWindow.MediaName(cmd), Tag = cmd });
        MediaBox.SelectedIndex = 0;
        foreach (var cmd in ObsCommands)
            ObsCommandBox.Items.Add(new ComboBoxItem { Content = MainWindow.ObsName(cmd), Tag = cmd });
        ObsCommandBox.SelectedIndex = 0;

        Title = existing == null ? "Nové tlačidlo" : "Upraviť tlačidlo";
        DeleteButton.Visibility = existing == null ? Visibility.Collapsed : Visibility.Visible;

        _icon = existing?.Icon;
        LabelBox.Text = existing?.Label ?? "";

        switch (existing?.Action)
        {
            case KeysAction k:
                SelectActionType("keys");
                if (Hotkey.TryParse(k.Keys, out var keys)) KeysBox.Hotkey = keys;
                break;
            case MediaAction m:
                SelectActionType("media");
                MediaBox.SelectedIndex = Math.Max(0, Array.IndexOf(MediaCommands, m.Command));
                break;
            case ObsAction ob:
                ObsCommandBox.SelectedIndex = Math.Max(0, Array.IndexOf(ObsCommands, ob.Command));
                ObsSceneBox.Text = ob.Scene ?? "";
                ObsSourceBox.Text = ob.Source ?? "";
                SelectActionType("obs");
                break;
            case OpenAction o:
                SelectActionType("open");
                TargetBox.Text = o.Target;
                ArgsBox.Text = o.Args ?? "";
                break;
            default:
                SelectActionType("open");
                break;
        }

        if (Hotkey.TryParse(existing?.Hotkey, out var hk)) GlobalHotkeyBox.Hotkey = hk;
        GlobalHotkeyBox.HotkeyChanged += (_, _) => UpdateHotkeyHint();

        UpdateIconPreview();
        UpdateHotkeyHint();

        Loaded += (_, _) =>
        {
            if (existing == null) TargetBox.Focus();
            else LabelBox.Focus();
        };
    }

    public DeckButton? Result { get; private set; }
    public bool DeleteRequested { get; private set; }

    // ───────────────────────── action type ─────────────────────────

    private string SelectedActionType => (ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "open";

    private void SelectActionType(string tag)
    {
        foreach (ComboBoxItem item in ActionTypeBox.Items)
        {
            if ((string)item.Tag == tag)
            {
                ActionTypeBox.SelectedItem = item;
                break;
            }
        }
        UpdateActionPanels();
    }

    private void ActionTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionPanels();

    private void UpdateActionPanels()
    {
        // SelectionChanged can fire during InitializeComponent, before the panels exist.
        if (OpenPanel == null || KeysPanel == null || MediaPanel == null || ObsPanel == null) return;
        var type = SelectedActionType;
        OpenPanel.Visibility = type == "open" ? Visibility.Visible : Visibility.Collapsed;
        KeysPanel.Visibility = type == "keys" ? Visibility.Visible : Visibility.Collapsed;
        MediaPanel.Visibility = type == "media" ? Visibility.Visible : Visibility.Collapsed;
        ObsPanel.Visibility = type == "obs" ? Visibility.Visible : Visibility.Collapsed;

        if (type == "obs")
        {
            UpdateObsRows();
            if (!_obsLoaded) _ = LoadObsListsAsync();
        }
    }

    // ───────────────────────── OBS ─────────────────────────

    private ObsCommand SelectedObsCommand =>
        (ObsCommandBox.SelectedItem as ComboBoxItem)?.Tag is ObsCommand c ? c : ObsCommand.SwitchScene;

    private void ObsCommandBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ObsSceneRow == null || ObsPanel == null) return; // during InitializeComponent
        UpdateObsRows();
        if (_obsLoaded) _ = LoadObsListsAsync(); // the source list depends on the command
    }

    private void ObsSceneBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingObsItems || ObsSourceRow == null) return;
        if (SelectedObsCommand == ObsCommand.ToggleSource && _obsLoaded)
        {
            // Show the sources of the newly picked scene (after the text box has been updated).
            Dispatcher.InvokeAsync(() => _ = LoadObsListsAsync());
        }
    }

    private void ObsRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadObsListsAsync();

    private void UpdateObsRows()
    {
        var cmd = SelectedObsCommand;
        bool scene = cmd is ObsCommand.SwitchScene or ObsCommand.ToggleSource;
        bool source = cmd is ObsCommand.ToggleMute or ObsCommand.ToggleSource;
        ObsSceneRow.Visibility = scene ? Visibility.Visible : Visibility.Collapsed;
        ObsSourceRow.Visibility = source ? Visibility.Visible : Visibility.Collapsed;
        ObsRefreshRow.Visibility = scene || source ? Visibility.Visible : Visibility.Collapsed;
        ObsSceneLabel.Text = cmd == ObsCommand.ToggleSource ? "Scéna (prázdne = aktuálna scéna)" : "Scéna";
        ObsSourceLabel.Text = cmd == ObsCommand.ToggleMute ? "Zdroj zvuku" : "Zdroj";
    }

    /// <summary>Fills the scene / source drop-downs from the running OBS. Names can always be typed by hand.</summary>
    private async Task LoadObsListsAsync()
    {
        int version = ++_obsLoadVersion;
        ObsStatus.ClearValue(TextBlock.ForegroundProperty);
        ObsStatus.Text = "Načítavam z OBS…";
        try
        {
            var cmd = SelectedObsCommand;
            var scenes = await _obs.GetScenesAsync();
            IReadOnlyList<string> sources = cmd switch
            {
                ObsCommand.ToggleMute => await _obs.GetInputsAsync(),
                ObsCommand.ToggleSource => await _obs.GetSceneSourcesAsync(ObsSceneBox.Text.Trim()),
                _ => Array.Empty<string>(),
            };
            if (version != _obsLoadVersion) return;

            SetObsItems(ObsSceneBox, scenes);
            SetObsItems(ObsSourceBox, sources);
            _obsLoaded = true;
            ObsStatus.Text = $"Načítané z OBS ({scenes.Count} scén).";
        }
        catch (Exception ex)
        {
            if (version != _obsLoadVersion) return;
            ObsStatus.Foreground = WarningBrush;
            ObsStatus.Text = ex.Message + " Názov môžeš napísať aj ručne.";
        }
    }

    private void SetObsItems(ComboBox box, IReadOnlyList<string> items)
    {
        _settingObsItems = true;
        try
        {
            var text = box.Text;
            box.ItemsSource = items;
            box.Text = text;
        }
        finally
        {
            _settingObsItems = false;
        }
    }

    // ───────────────────────── open target ─────────────────────────

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Vyber program alebo súbor",
            Filter = "Všetky súbory|*.*|Programy a odkazy|*.exe;*.lnk;*.bat;*.cmd",
            DereferenceLinks = false, // keep .lnk as .lnk (it carries arguments and the right icon)
        };
        if (dlg.ShowDialog(this) == true)
        {
            TargetBox.Text = dlg.FileName;
            AutoFill(dlg.FileName);
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Vyber priečinok" };
        if (dlg.ShowDialog(this) == true)
        {
            TargetBox.Text = dlg.FolderName;
            AutoFill(dlg.FolderName);
        }
    }

    /// <summary>Fills in the label and icon from a picked path, unless the user already set them.</summary>
    private void AutoFill(string path)
    {
        if (string.IsNullOrWhiteSpace(LabelBox.Text)) LabelBox.Text = MainWindow.LabelForPath(path);
        if (_icon == null)
        {
            _icon = _icons.CreateFrom(path);
            UpdateIconPreview();
        }
    }

    // ───────────────────────── icon ─────────────────────────

    private void PickIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Vyber ikonu",
            Filter = "Obrázky a programy|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.webp;*.exe;*.lnk;*.dll|Všetky súbory|*.*",
            DereferenceLinks = false,
        };
        if (dlg.ShowDialog(this) == true) SetIconFrom(dlg.FileName);
    }

    private void ClearIcon_Click(object sender, RoutedEventArgs e)
    {
        _icon = null;
        UpdateIconPreview();
    }

    private void IconDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void IconDrop_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) SetIconFrom(files[0]);
    }

    private void SetIconFrom(string path)
    {
        var icon = _icons.CreateFrom(path);
        if (icon == null)
        {
            Warn("Z tohto súboru sa nepodarilo vytvoriť ikonu.");
            return;
        }
        _icon = icon;
        UpdateIconPreview();
    }

    private void UpdateIconPreview()
    {
        var img = _icons.Load(_icon);
        IconImage.Source = img;
        IconPlaceholder.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── global hotkey ─────────────────────────

    private void ClearHotkey_Click(object sender, RoutedEventArgs e) => GlobalHotkeyBox.Hotkey = null;

    private void UpdateHotkeyHint()
    {
        var hk = GlobalHotkeyBox.Hotkey;
        HotkeyHint.Foreground = WarningBrush;
        HotkeyHint.Opacity = 1;

        if (hk == null)
        {
            HotkeyHint.Text = "Nepovinné. Skratka spustí toto tlačidlo, aj keď je okno skryté.";
            HotkeyHint.ClearValue(TextBlock.ForegroundProperty);
            HotkeyHint.Opacity = 0.6;
        }
        else if (_config.FindHotkeyConflict(hk.Value, _existing?.Id) is { } other)
        {
            HotkeyHint.Text = $"Túto skratku už má tlačidlo „{other.Label ?? "bez popisu"}“.";
        }
        else if (!hk.Value.HasModifiers)
        {
            HotkeyHint.Text = $"Pozor: kláves {hk} bez Ctrl/Alt/Shift prestane fungovať normálne, kým appka beží. " +
                              "Hodí sa napr. F13–F24 alebo numerická klávesnica.";
        }
        else
        {
            HotkeyHint.Text = "Skratka spustí toto tlačidlo, aj keď je okno skryté.";
            HotkeyHint.ClearValue(TextBlock.ForegroundProperty);
            HotkeyHint.Opacity = 0.6;
        }
    }

    // ───────────────────────── save / delete ─────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DeckAction action;
        switch (SelectedActionType)
        {
            case "keys":
                if (KeysBox.Hotkey is not { } keys)
                {
                    Warn("Nastav skratku, ktorú má tlačidlo poslať.");
                    return;
                }
                action = new KeysAction { Keys = keys.ToString() };
                break;

            case "obs":
                var obsCmd = SelectedObsCommand;
                var scene = ObsSceneBox.Text.Trim();
                var source = ObsSourceBox.Text.Trim();
                if (obsCmd == ObsCommand.SwitchScene && scene.Length == 0)
                {
                    Warn("Vyber scénu, na ktorú sa má OBS prepnúť.");
                    return;
                }
                if (obsCmd is ObsCommand.ToggleMute or ObsCommand.ToggleSource && source.Length == 0)
                {
                    Warn("Vyber zdroj, ktorý má tlačidlo ovládať.");
                    return;
                }
                action = new ObsAction
                {
                    Command = obsCmd,
                    Scene = obsCmd is ObsCommand.SwitchScene or ObsCommand.ToggleSource && scene.Length > 0 ? scene : null,
                    Source = obsCmd is ObsCommand.ToggleMute or ObsCommand.ToggleSource ? source : null,
                };
                break;

            case "media":
                var cmd = (MediaBox.SelectedItem as ComboBoxItem)?.Tag is MediaCommand c ? c : MediaCommand.PlayPause;
                action = new MediaAction { Command = cmd };
                break;

            default:
                var target = TargetBox.Text.Trim();
                if (target.Length == 0)
                {
                    Warn("Zadaj, čo sa má otvoriť – program, súbor, priečinok alebo web.");
                    return;
                }
                action = new OpenAction
                {
                    Target = target,
                    Args = string.IsNullOrWhiteSpace(ArgsBox.Text) ? null : ArgsBox.Text.Trim(),
                };
                break;
        }

        var hotkey = GlobalHotkeyBox.Hotkey;
        if (hotkey is { } h && _config.FindHotkeyConflict(h, _existing?.Id) is { } other)
        {
            Warn($"Skratku {h} už má tlačidlo „{other.Label ?? "bez popisu"}“.");
            return;
        }

        // No icon picked: use the program's / file's own icon if there is one.
        if (_icon == null && action is OpenAction open)
        {
            var path = Environment.ExpandEnvironmentVariables(open.Target.Trim('"'));
            if (File.Exists(path) || Directory.Exists(path)) _icon = _icons.CreateFrom(path);
        }

        Result = new DeckButton
        {
            Id = _existing?.Id ?? DeckButton.NewId(),
            Row = _row,
            Col = _col,
            Label = string.IsNullOrWhiteSpace(LabelBox.Text) ? DefaultLabel(action) : LabelBox.Text.Trim(),
            Icon = _icon,
            Hotkey = hotkey?.ToString(),
            Action = action,
        };
        DialogResult = true;
    }

    /// <summary>A sensible label when the user leaves it empty (mostly for OBS buttons, which have no icon).</summary>
    private static string? DefaultLabel(DeckAction action) => action switch
    {
        ObsAction { Command: ObsCommand.SwitchScene } o => o.Scene,
        ObsAction { Command: ObsCommand.ToggleMute or ObsCommand.ToggleSource } o => o.Source,
        ObsAction { Command: ObsCommand.ToggleRecord } => "REC",
        ObsAction { Command: ObsCommand.ToggleStream } => "LIVE",
        ObsAction { Command: ObsCommand.SaveReplay } => "Replay",
        _ => null,
    };

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested = true;
        DialogResult = true;
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, "Virtual Stream Deck", MessageBoxButton.OK, MessageBoxImage.Warning);
}
