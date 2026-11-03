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
        ProfileBox.ItemsSource = config.Profiles;
        ProfileBox.SelectedItem = config.Profiles.Find(p => p.Id != config.ActiveProfile.Id) ?? config.ActiveProfile;

        Title = existing == null ? L.T("Nové tlačidlo", "New button") : L.T("Upraviť tlačidlo", "Edit button");
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
            case ProfileAction pa:
                ProfileBox.SelectedItem = config.FindProfile(pa.ProfileId) ?? ProfileBox.SelectedItem;
                SelectActionType("profile");
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
        if (OpenPanel == null || KeysPanel == null || MediaPanel == null || ObsPanel == null || ProfilePanel == null) return;
        var type = SelectedActionType;
        OpenPanel.Visibility = type == "open" ? Visibility.Visible : Visibility.Collapsed;
        KeysPanel.Visibility = type == "keys" ? Visibility.Visible : Visibility.Collapsed;
        MediaPanel.Visibility = type == "media" ? Visibility.Visible : Visibility.Collapsed;
        ObsPanel.Visibility = type == "obs" ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = type == "profile" ? Visibility.Visible : Visibility.Collapsed;

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
        ObsSceneLabel.Text = cmd == ObsCommand.ToggleSource
            ? L.T("Scéna (prázdne = aktuálna scéna)", "Scene (empty = current scene)")
            : L.T("Scéna", "Scene");
        ObsSourceLabel.Text = cmd == ObsCommand.ToggleMute ? L.T("Zdroj zvuku", "Audio source") : L.T("Zdroj", "Source");
    }

    /// <summary>Fills the scene / source drop-downs from the running OBS. Names can always be typed by hand.</summary>
    private async Task LoadObsListsAsync()
    {
        int version = ++_obsLoadVersion;
        ObsStatus.ClearValue(TextBlock.ForegroundProperty);
        ObsStatus.Text = L.T("Načítavam z OBS…", "Loading from OBS…");
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
            ObsStatus.Text = L.T($"Načítané z OBS ({scenes.Count} scén).", $"Loaded from OBS ({scenes.Count} scenes).");
        }
        catch (Exception ex)
        {
            if (version != _obsLoadVersion) return;
            ObsStatus.Foreground = WarningBrush;
            ObsStatus.Text = ex.Message + L.T(" Názov môžeš napísať aj ručne.", " You can also type the name.");
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
            Title = L.T("Vyber program alebo súbor", "Choose a program or file"),
            Filter = L.T("Všetky súbory", "All files") + "|*.*|" + L.T("Programy a odkazy", "Programs and shortcuts") + "|*.exe;*.lnk;*.bat;*.cmd",
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
        var dlg = new OpenFolderDialog { Title = L.T("Vyber priečinok", "Choose a folder") };
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
            Title = L.T("Vyber ikonu", "Choose an icon"),
            Filter = L.T("Obrázky a programy", "Pictures and programs") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.webp;*.exe;*.lnk;*.dll|" +
                     L.T("Všetky súbory", "All files") + "|*.*",
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
            Warn(L.T("Z tohto súboru sa nepodarilo vytvoriť ikonu.", "Could not make an icon from this file."));
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
            HotkeyHint.Text = L.T("Nepovinné. Skratka spustí toto tlačidlo, aj keď je okno skryté.", "Optional. The hotkey runs this button even when the window is hidden.");
            HotkeyHint.ClearValue(TextBlock.ForegroundProperty);
            HotkeyHint.Opacity = 0.6;
        }
        else if (_config.FindHotkeyConflict(hk.Value, _existing?.Id) is { } other)
        {
            HotkeyHint.Text = L.T("Túto skratku už má tlačidlo ", "This hotkey is already used by the button ") + MainWindow.Quote(other.Label ?? L.T("bez popisu", "without label")) + ".";
        }
        else if (!hk.Value.HasModifiers)
        {
            HotkeyHint.Text = L.T($"Pozor: kláves {hk} bez Ctrl/Alt/Shift prestane fungovať normálne, kým appka beží. " +
                                  "Hodí sa napr. F13–F24 alebo numerická klávesnica.",
                                  $"Note: {hk} without Ctrl/Alt/Shift stops working normally while the app runs. " +
                                  "Good choices are F13–F24 or the numeric keypad.");
        }
        else
        {
            HotkeyHint.Text = L.T("Skratka spustí toto tlačidlo, aj keď je okno skryté.", "The hotkey runs this button even when the window is hidden.");
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
                    Warn(L.T("Nastav skratku, ktorú má tlačidlo poslať.", "Set the key combination the button should send."));
                    return;
                }
                action = new KeysAction { Keys = keys.ToString() };
                break;

            case "profile":
                if (ProfileBox.SelectedItem is not DeckProfile targetProfile)
                {
                    Warn(L.T("Vyber profil, na ktorý sa má prepnúť.", "Choose the profile to switch to."));
                    return;
                }
                action = new ProfileAction { ProfileId = targetProfile.Id };
                break;

            case "obs":
                var obsCmd = SelectedObsCommand;
                var scene = ObsSceneBox.Text.Trim();
                var source = ObsSourceBox.Text.Trim();
                if (obsCmd == ObsCommand.SwitchScene && scene.Length == 0)
                {
                    Warn(L.T("Vyber scénu, na ktorú sa má OBS prepnúť.", "Choose the scene OBS should switch to."));
                    return;
                }
                if (obsCmd is ObsCommand.ToggleMute or ObsCommand.ToggleSource && source.Length == 0)
                {
                    Warn(L.T("Vyber zdroj, ktorý má tlačidlo ovládať.", "Choose the source the button should control."));
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
                    Warn(L.T("Zadaj, čo sa má otvoriť – program, súbor, priečinok alebo web.", "Enter what to open – a program, file, folder or website."));
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
            Warn(L.T($"Skratku {h} už má tlačidlo ", $"{h} is already used by the button ") + MainWindow.Quote(other.Label ?? L.T("bez popisu", "without label")) + ".");
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
            Label = string.IsNullOrWhiteSpace(LabelBox.Text)
                ? (action is ProfileAction pAct ? _config.FindProfile(pAct.ProfileId)?.Name : DefaultLabel(action))
                : LabelBox.Text.Trim(),
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
