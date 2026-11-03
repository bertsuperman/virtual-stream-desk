using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;

namespace VirtualStreamDeck.App.UI;

/// <summary>Edit / create one button. Result is set when the user saves (or asks to delete).</summary>
internal sealed class ButtonEditorWindow : Window
{
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

    private static readonly string[] ActionTypes = { "open", "keys", "media", "obs", "profile" };

    private readonly ObsClient _obs;
    private readonly DeckConfig _config;
    private readonly DeckButton? _existing;
    private readonly int _row, _col;
    private readonly IconService _icons;
    private string? _icon;
    private bool _obsLoaded;
    private int _obsLoadVersion;

    private readonly Image _iconImage = new() { Width = 52, Height = 52, Stretch = Stretch.Uniform };
    private readonly Control _iconPlaceholder = Icons.Make("image", 28, Ui.EmptyGlyph, 1.6);
    private readonly TextBox _label = new();
    private readonly ComboBox _actionType = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _target = new();
    private readonly TextBox _args = new();
    private readonly HotkeyBox _keys = new();
    private readonly ComboBox _media = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _profile = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _obsCommand = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AutoCompleteBox _obsScene = NewAutoComplete();
    private readonly AutoCompleteBox _obsSource = NewAutoComplete();
    private readonly TextBlock _obsSceneLabel = Ui.Text("", opacity: 0.85, margin: new Thickness(0, 0, 0, 6));
    private readonly TextBlock _obsSourceLabel = Ui.Text("", opacity: 0.85, margin: new Thickness(0, 0, 0, 6));
    private readonly TextBlock _obsStatus = Ui.Hint("", new Thickness(0));
    private readonly HotkeyBox _hotkey = new();
    private readonly TextBlock _hotkeyHint = Ui.Hint("", new Thickness(0, 6, 0, 18));
    private readonly Dictionary<string, Control> _panels = new();
    private StackPanel _obsSceneRow = null!, _obsSourceRow = null!;
    private DockPanel _obsRefreshRow = null!;

    public ButtonEditorWindow(DeckConfig config, DeckButton? existing, int row, int col, IconService icons, ObsClient obs)
    {
        _obs = obs;
        _config = config;
        _existing = existing;
        _row = row;
        _col = col;
        _icons = icons;

        Title = existing == null ? L.T("Nové tlačidlo", "New button") : L.T("Upraviť tlačidlo", "Edit button");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.PanelBg;

        foreach (var cmd in MediaCommands) _media.Items.Add(MainWindow.MediaName(cmd));
        _media.SelectedIndex = 0;
        foreach (var cmd in ObsCommands) _obsCommand.Items.Add(MainWindow.ObsName(cmd));
        _obsCommand.SelectedIndex = 0;
        foreach (var p in config.Profiles) _profile.Items.Add(p.Name);
        var otherProfile = config.Profiles.FindIndex(p => p.Id != config.ActiveProfile.Id);
        _profile.SelectedIndex = otherProfile >= 0 ? otherProfile : 0;
        foreach (var (sk, en) in new[]
                 {
                     ("Otvoriť program, súbor alebo web", "Open a program, file or website"),
                     ("Stlačiť klávesovú skratku", "Press a key combination"),
                     ("Médiá a hlasitosť", "Media and volume"),
                     ("OBS Studio", "OBS Studio"),
                     ("Prepnúť profil", "Switch profile"),
                 })
            _actionType.Items.Add(L.T(sk, en));

        Content = BuildContent();

        _icon = existing?.Icon;
        _label.Text = existing?.Label ?? "";
        switch (existing?.Action)
        {
            case KeysAction k:
                SelectActionType("keys");
                if (Hotkey.TryParse(k.Keys, out var keys)) _keys.Hotkey = keys;
                break;
            case MediaAction m:
                SelectActionType("media");
                _media.SelectedIndex = Math.Max(0, Array.IndexOf(MediaCommands, m.Command));
                break;
            case ObsAction ob:
                _obsCommand.SelectedIndex = Math.Max(0, Array.IndexOf(ObsCommands, ob.Command));
                _obsScene.Text = ob.Scene ?? "";
                _obsSource.Text = ob.Source ?? "";
                SelectActionType("obs");
                break;
            case ProfileAction pa:
                var index = config.Profiles.FindIndex(p => p.Id == pa.ProfileId);
                if (index >= 0) _profile.SelectedIndex = index;
                SelectActionType("profile");
                break;
            case OpenAction o:
                SelectActionType("open");
                _target.Text = o.Target;
                _args.Text = o.Args ?? "";
                break;
            default:
                SelectActionType("open");
                break;
        }

        if (Hotkey.TryParse(existing?.Hotkey, out var hk)) _hotkey.Hotkey = hk;
        _hotkey.HotkeyChanged += (_, _) => UpdateHotkeyHint();
        _actionType.SelectionChanged += (_, _) => UpdateActionPanels();
        _obsCommand.SelectionChanged += (_, _) =>
        {
            UpdateObsRows();
            if (_obsLoaded) _ = LoadObsListsAsync(); // the source list depends on the command
        };
        _obsScene.SelectionChanged += (_, _) =>
        {
            // Show the sources of the newly picked scene.
            if (SelectedObsCommand == ObsCommand.ToggleSource && _obsLoaded) Dispatcher.UIThread.Post(() => _ = LoadObsListsAsync());
        };

        UpdateIconPreview();
        UpdateHotkeyHint();
        Opened += (_, _) =>
        {
            if (existing == null) _target.Focus();
            else _label.Focus();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    public DeckButton? Result { get; private set; }
    public bool DeleteRequested { get; private set; }

    private static AutoCompleteBox NewAutoComplete() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        FilterMode = AutoCompleteFilterMode.Contains,
        MinimumPrefixLength = 0,
    };

    // ───────────────────────── layout ─────────────────────────

    private Control BuildContent()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(24, 20, 24, 20) };
        int row = 0;
        void Add(Control? label, Control value, double bottom = 14)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (label != null)
            {
                label.VerticalAlignment = VerticalAlignment.Top;
                label.Margin = new Thickness(0, 6, 12, 0);
                Grid.SetRow(label, row);
                grid.Children.Add(label);
            }
            value.Margin = new Thickness(0, 0, 0, bottom);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            row++;
        }

        // Icon
        var iconDrop = new Border
        {
            Width = 76,
            Height = 76,
            CornerRadius = new CornerRadius(12),
            Background = Ui.Tile,
            BorderBrush = Ui.B("#3A3A42"),
            BorderThickness = new Thickness(1),
            Child = new Panel { Children = { _iconImage, _iconPlaceholder } },
        };
        ToolTip.SetTip(iconDrop, L.T("Sem môžeš pretiahnuť obrázok alebo program", "You can drop a picture or program here"));
        DragDrop.SetAllowDrop(iconDrop, true);
        iconDrop.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        iconDrop.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var path = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null);
            if (path != null) SetIconFrom(path);
        });
        Add(Ui.FieldLabel(L.T("Ikona", "Icon")), new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children =
            {
                iconDrop,
                Ui.Column(8,
                    Ui.Button(L.T("Vybrať obrázok…", "Choose picture…"), PickIcon, 160),
                    Ui.Button(L.T("Bez ikony", "No icon"), () =>
                    {
                        _icon = null;
                        UpdateIconPreview();
                    }, 160)),
            },
        });

        Add(Ui.FieldLabel(L.T("Popis", "Label")), _label);
        Add(Ui.FieldLabel(L.T("Akcia", "Action")), _actionType);

        // Action details
        var open = Ui.Column(0,
            Dock(_target, Ui.Button(L.T("Súbor…", "File…"), BrowseFile), Ui.Button(L.T("Priečinok…", "Folder…"), BrowseFolder)),
            Ui.Hint(AppInfo.IsMac
                ? L.T("Appka, súbor, priečinok alebo adresa webu (https://…).", "App, file, folder or web address (https://…).")
                : L.T("Program, odkaz, súbor, priečinok alebo adresa webu (https://…).", "Program, shortcut, file, folder or web address (https://…).")),
            Ui.Text(L.T("Argumenty (nepovinné)", "Arguments (optional)"), opacity: 0.85, margin: new Thickness(0, 12, 0, 6)),
            _args);
        var keys = Ui.Column(0, _keys,
            Ui.Hint(AppInfo.IsMac
                ? L.T("Skratka sa pošle do appky, v ktorej práve pracuješ (napr. ⇧⌘M pre mute v Teams).",
                    "The keys go to the app you are working in (e.g. ⇧⌘M to mute in Teams).")
                : L.T("Skratka sa pošle do okna, v ktorom práve pracuješ (napr. Ctrl+Shift+M pre mute v Teams).",
                    "The keys go to the window you are working in (e.g. Ctrl+Shift+M to mute in Teams).")));
        var media = Ui.Column(0, _media);
        var profile = Ui.Column(0, _profile,
            Ui.Hint(L.T("Ako priečinok na Stream Decku: v cieľovom profile si daj tlačidlo späť. Nové profily vytvoríš v Nastavenia → Profily.",
                "Like a folder on a Stream Deck: put a button back in the target profile. Create new profiles in Settings → Profiles.")));
        _obsSceneRow = Ui.Column(0, _obsSceneLabel, _obsScene);
        _obsSceneRow.Margin = new Thickness(0, 12, 0, 0);
        _obsSourceRow = Ui.Column(0, _obsSourceLabel, _obsSource);
        _obsSourceRow.Margin = new Thickness(0, 12, 0, 0);
        _obsRefreshRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var refresh = Ui.Button(L.T("Načítať z OBS", "Load from OBS"), () => _ = LoadObsListsAsync());
        DockPanel.SetDock(refresh, Avalonia.Controls.Dock.Right);
        refresh.Margin = new Thickness(10, 0, 0, 0);
        _obsStatus.VerticalAlignment = VerticalAlignment.Center;
        _obsRefreshRow.Children.Add(refresh);
        _obsRefreshRow.Children.Add(_obsStatus);
        var obsPanel = Ui.Column(0, _obsCommand, _obsSceneRow, _obsSourceRow, _obsRefreshRow);

        _panels["open"] = open;
        _panels["keys"] = keys;
        _panels["media"] = media;
        _panels["obs"] = obsPanel;
        _panels["profile"] = profile;
        var details = new Panel();
        foreach (var p in _panels.Values) details.Children.Add(p);
        Add(null, details, 18);

        // Global hotkey
        var clear = Ui.Button(L.T("Zrušiť", "Clear"), () => _hotkey.Hotkey = null);
        Add(Ui.FieldLabel(L.T("Klávesová skratka", "Hotkey")), Dock(_hotkey, clear), 0);
        Add(null, _hotkeyHint, 0);

        // Buttons
        var bottom = new DockPanel { LastChildFill = false };
        if (_existing != null)
        {
            var delete = Ui.Button(L.T("Vymazať tlačidlo", "Delete button"), () =>
            {
                DeleteRequested = true;
                Close(true);
            });
            DockPanel.SetDock(delete, Avalonia.Controls.Dock.Left);
            bottom.Children.Add(delete);
        }
        var cancel = Ui.Button(L.T("Zrušiť", "Cancel"), () => Close(false), 90);
        cancel.IsCancel = true;
        var save = Ui.AccentButton(L.T("Uložiť", "Save"), Save, 90);
        save.IsDefault = true;
        DockPanel.SetDock(cancel, Avalonia.Controls.Dock.Right);
        DockPanel.SetDock(save, Avalonia.Controls.Dock.Right);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        bottom.Children.Add(cancel);
        bottom.Children.Add(save);
        var outer = new StackPanel { Children = { grid } };
        bottom.Margin = new Thickness(24, 0, 24, 20);
        outer.Children.Add(bottom);
        return outer;
    }

    /// <summary>A text box with buttons on its right.</summary>
    private static DockPanel Dock(Control fill, params Control[] right)
    {
        // The first right-docked child is the rightmost, so add them from the last one.
        var dock = new DockPanel();
        for (int i = right.Length - 1; i >= 0; i--)
        {
            DockPanel.SetDock(right[i], Avalonia.Controls.Dock.Right);
            right[i].Margin = new Thickness(6, 0, 0, 0);
            dock.Children.Add(right[i]);
        }
        dock.Children.Add(fill);
        return dock;
    }

    // ───────────────────────── action type ─────────────────────────

    private string SelectedActionType => _actionType.SelectedIndex is >= 0 and < 5 ? ActionTypes[_actionType.SelectedIndex] : "open";

    private void SelectActionType(string tag)
    {
        _actionType.SelectedIndex = Array.IndexOf(ActionTypes, tag);
        UpdateActionPanels();
    }

    private void UpdateActionPanels()
    {
        var type = SelectedActionType;
        foreach (var (key, panel) in _panels) panel.IsVisible = key == type;
        if (type == "obs")
        {
            UpdateObsRows();
            if (!_obsLoaded) _ = LoadObsListsAsync();
        }
    }

    // ───────────────────────── OBS ─────────────────────────

    private ObsCommand SelectedObsCommand => _obsCommand.SelectedIndex >= 0 ? ObsCommands[_obsCommand.SelectedIndex] : ObsCommand.SwitchScene;

    private void UpdateObsRows()
    {
        var cmd = SelectedObsCommand;
        bool scene = cmd is ObsCommand.SwitchScene or ObsCommand.ToggleSource;
        bool source = cmd is ObsCommand.ToggleMute or ObsCommand.ToggleSource;
        _obsSceneRow.IsVisible = scene;
        _obsSourceRow.IsVisible = source;
        _obsRefreshRow.IsVisible = scene || source;
        _obsSceneLabel.Text = cmd == ObsCommand.ToggleSource
            ? L.T("Scéna (prázdne = aktuálna scéna)", "Scene (empty = current scene)")
            : L.T("Scéna", "Scene");
        _obsSourceLabel.Text = cmd == ObsCommand.ToggleMute ? L.T("Zdroj zvuku", "Audio source") : L.T("Zdroj", "Source");
    }

    /// <summary>Fills the scene / source suggestions from the running OBS. Names can always be typed by hand.</summary>
    private async Task LoadObsListsAsync()
    {
        int version = ++_obsLoadVersion;
        _obsStatus.Foreground = Ui.Muted;
        _obsStatus.Text = L.T("Načítavam z OBS…", "Loading from OBS…");
        try
        {
            var cmd = SelectedObsCommand;
            var scenes = await _obs.GetScenesAsync();
            IReadOnlyList<string> sources = cmd switch
            {
                ObsCommand.ToggleMute => await _obs.GetInputsAsync(),
                ObsCommand.ToggleSource => await _obs.GetSceneSourcesAsync((_obsScene.Text ?? "").Trim()),
                _ => Array.Empty<string>(),
            };
            if (version != _obsLoadVersion) return;
            _obsScene.ItemsSource = scenes;
            _obsSource.ItemsSource = sources;
            _obsLoaded = true;
            _obsStatus.Text = L.T($"Načítané z OBS ({scenes.Count} scén).", $"Loaded from OBS ({scenes.Count} scenes).");
        }
        catch (Exception ex)
        {
            if (version != _obsLoadVersion) return;
            _obsStatus.Foreground = Ui.Warn;
            _obsStatus.Text = ex.Message + L.T(" Názov môžeš napísať aj ručne.", " You can also type the name.");
        }
    }

    // ───────────────────────── open target ─────────────────────────

    private async void BrowseFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = AppInfo.IsMac ? L.T("Vyber appku alebo súbor", "Choose an app or file") : L.T("Vyber program alebo súbor", "Choose a program or file"),
            AllowMultiple = false,
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            _target.Text = path;
            AutoFill(path);
        }
    }

    private async void BrowseFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.T("Vyber priečinok", "Choose a folder"),
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            _target.Text = path;
            AutoFill(path);
        }
    }

    /// <summary>Fills in the label and icon from a picked path, unless the user already set them.</summary>
    private void AutoFill(string path)
    {
        if (string.IsNullOrWhiteSpace(_label.Text)) _label.Text = MainWindow.LabelForPath(path);
        if (_icon == null)
        {
            _icon = _icons.CreateFrom(path);
            UpdateIconPreview();
        }
    }

    // ───────────────────────── icon ─────────────────────────

    private async void PickIcon()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Vyber ikonu", "Choose an icon"),
            AllowMultiple = false,
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) SetIconFrom(path);
    }

    private async void SetIconFrom(string path)
    {
        var icon = _icons.CreateFrom(path);
        if (icon == null)
        {
            await Warn(L.T("Z tohto súboru sa nepodarilo vytvoriť ikonu.", "Could not make an icon from this file."));
            return;
        }
        _icon = icon;
        UpdateIconPreview();
    }

    private void UpdateIconPreview()
    {
        var img = _icons.Load(_icon);
        _iconImage.Source = img;
        _iconPlaceholder.IsVisible = img == null;
    }

    // ───────────────────────── global hotkey ─────────────────────────

    private void UpdateHotkeyHint()
    {
        var hk = _hotkey.Hotkey;
        _hotkeyHint.Foreground = Ui.Warn;
        if (hk == null)
        {
            _hotkeyHint.Text = L.T("Nepovinné. Skratka spustí toto tlačidlo, aj keď je okno skryté.", "Optional. The hotkey runs this button even when the window is hidden.");
            _hotkeyHint.Foreground = Ui.Muted;
        }
        else if (_config.FindHotkeyConflict(hk.Value, _existing?.Id) is { } other)
        {
            _hotkeyHint.Text = L.T("Túto skratku už má tlačidlo ", "This hotkey is already used by the button ") +
                               MainWindow.Quote(other.Label ?? L.T("bez popisu", "without label")) + ".";
        }
        else if (AppInfo.IsMac && Core.Mac.MacKeys.ToMac(hk.Value.VirtualKey) == null)
        {
            _hotkeyHint.Text = L.T("Tento kláves na Macu nie je. Vyber iný.", "This key does not exist on a Mac. Pick another one.");
        }
        else if (!hk.Value.HasModifiers)
        {
            var name = HotkeyBox.Describe(hk.Value);
            _hotkeyHint.Text = L.T($"Pozor: kláves {name} bez modifikátora prestane fungovať normálne, kým appka beží. Hodí sa napr. F13–F20 alebo numerická klávesnica.",
                $"Note: {name} without a modifier stops working normally while the app runs. Good choices are F13–F20 or the numeric keypad.");
        }
        else
        {
            _hotkeyHint.Text = L.T("Skratka spustí toto tlačidlo, aj keď je okno skryté.", "The hotkey runs this button even when the window is hidden.");
            _hotkeyHint.Foreground = Ui.Muted;
        }
    }

    // ───────────────────────── save ─────────────────────────

    private async void Save()
    {
        DeckAction action;
        switch (SelectedActionType)
        {
            case "keys":
                if (_keys.Hotkey is not { } keys)
                {
                    await Warn(L.T("Nastav skratku, ktorú má tlačidlo poslať.", "Set the key combination the button should send."));
                    return;
                }
                action = new KeysAction { Keys = keys.ToString() };
                break;

            case "profile":
                if (_profile.SelectedIndex < 0 || _profile.SelectedIndex >= _config.Profiles.Count)
                {
                    await Warn(L.T("Vyber profil, na ktorý sa má prepnúť.", "Choose the profile to switch to."));
                    return;
                }
                action = new ProfileAction { ProfileId = _config.Profiles[_profile.SelectedIndex].Id };
                break;

            case "obs":
                var obsCmd = SelectedObsCommand;
                var scene = (_obsScene.Text ?? "").Trim();
                var source = (_obsSource.Text ?? "").Trim();
                if (obsCmd == ObsCommand.SwitchScene && scene.Length == 0)
                {
                    await Warn(L.T("Vyber scénu, na ktorú sa má OBS prepnúť.", "Choose the scene OBS should switch to."));
                    return;
                }
                if (obsCmd is ObsCommand.ToggleMute or ObsCommand.ToggleSource && source.Length == 0)
                {
                    await Warn(L.T("Vyber zdroj, ktorý má tlačidlo ovládať.", "Choose the source the button should control."));
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
                action = new MediaAction { Command = MediaCommands[Math.Max(0, _media.SelectedIndex)] };
                break;

            default:
                var target = (_target.Text ?? "").Trim();
                if (target.Length == 0)
                {
                    await Warn(L.T("Zadaj, čo sa má otvoriť – program, súbor, priečinok alebo web.", "Enter what to open – a program, file, folder or website."));
                    return;
                }
                action = new OpenAction
                {
                    Target = target,
                    Args = string.IsNullOrWhiteSpace(_args.Text) ? null : _args.Text.Trim(),
                };
                break;
        }

        var hotkey = _hotkey.Hotkey;
        if (hotkey is { } h && _config.FindHotkeyConflict(h, _existing?.Id) is { } other)
        {
            await Warn(L.T($"Skratku {HotkeyBox.Describe(h)} už má tlačidlo ", $"{HotkeyBox.Describe(h)} is already used by the button ") +
                       MainWindow.Quote(other.Label ?? L.T("bez popisu", "without label")) + ".");
            return;
        }

        // No icon picked: use the program's / file's own icon if there is one.
        if (_icon == null && action is OpenAction open)
        {
            var path = Environment.ExpandEnvironmentVariables(open.Target.Trim('"'));
            if (File.Exists(path) || Directory.Exists(path)) _icon = _icons.CreateFrom(path);
        }

        var label = _label.Text;
        Result = new DeckButton
        {
            Id = _existing?.Id ?? DeckButton.NewId(),
            Row = _row,
            Col = _col,
            Label = string.IsNullOrWhiteSpace(label)
                ? (action is ProfileAction pAct ? _config.FindProfile(pAct.ProfileId)?.Name : DefaultLabel(action))
                : label.Trim(),
            Icon = _icon,
            Hotkey = hotkey?.ToString(),
            Action = action,
        };
        Close(true);
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

    private Task Warn(string text) => Dialogs.InfoAsync(this, AppInfo.ProductName, text);
}
