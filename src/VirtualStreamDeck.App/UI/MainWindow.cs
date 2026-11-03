using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;
using VirtualStreamDeck.Core.Remote;

namespace VirtualStreamDeck.App.UI;

/// <summary>The deck: side panel (collapsible) | rail with icons | grid of buttons.</summary>
internal sealed class MainWindow : Window
{
    private const string InternalDragFormat = "VirtualStreamDeck.ButtonId";

    private readonly Deck _deck;
    private readonly ObsClient _obs;
    private readonly UpdateService _updates;
    private readonly HotkeyService _hotkeys;
    private readonly IconService _icons;
    private readonly SettingsPanel _panel;
    private readonly Dictionary<string, Border> _tiles = new();
    private readonly UniformGrid _grid = new() { Margin = new Thickness(8) };
    private readonly Border _toast;
    private readonly TextBlock _toastText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 360 };
    private readonly StackPanel _rail = new() { Margin = new Thickness(5, 8, 5, 8), Spacing = 6 };
    private Border _profilesRail = null!, _settingsRail = null!, _guideRail = null!, _updateRail = null!;
    private bool _dialogOpen;
    private bool _exiting;
    private int _statusVersion;
    private string _lastSettingsSection = SettingsPanel.Layout;
    private string? _announcedVersion;
    private int _lastToastPercent = -1;

    // Tile press / drag state
    private Border? _pressedTile;
    private Point _pressPoint;

    private readonly DispatcherTimer _savePositionTimer;

    public MainWindow(Deck deck, ObsClient obs, RemoteServer remote, UpdateService updates, HotkeyService hotkeys)
    {
        _deck = deck;
        _obs = obs;
        _updates = updates;
        _hotkeys = hotkeys;
        _icons = new IconService(deck.Store);

        Title = "Stream Deck";
        Background = Ui.WindowBg;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowActivated = false;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://VirtualStreamDeck/Assets/app.png"))); }
        catch { /* no icon in tests */ }

        _panel = new SettingsPanel(deck, obs, remote, updates) { IsVisible = false };
        _panel.CollapseRequested += () => SetPanel(null);

        _toast = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(10),
            Padding = new Thickness(12, 7),
            CornerRadius = new CornerRadius(8),
            Background = Ui.ToastBg,
            BorderThickness = new Thickness(1),
            IsVisible = false,
            IsHitTestVisible = false,
            Child = _toastText,
        };

        var railBorder = new Border
        {
            Background = Ui.PanelBg,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _rail,
        };
        var deckArea = new Grid { VerticalAlignment = VerticalAlignment.Top, Children = { _grid, _toast } };

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto") };
        Grid.SetColumn(railBorder, 1);
        Grid.SetColumn(deckArea, 2);
        root.Children.Add(_panel);
        root.Children.Add(railBorder);
        root.Children.Add(deckArea);
        Content = root;

        BuildRail();

        // A profile switch from the phone arrives on a background thread.
        deck.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        _hotkeys.Pressed += Press;
        updates.Changed += OnUpdateChanged;
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            BuildRail();
            Rebuild(); // tooltips and the title are built in code
            OnUpdateChanged();
        });

        _savePositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _savePositionTimer.Tick += (_, _) =>
        {
            _savePositionTimer.Stop();
            if (WindowState != WindowState.Normal || !IsVisible) return;
            // Store where the deck is, without the side panel on its left.
            int left = Position.X + (_panel.IsVisible ? PanelWidthPx : 0);
            int top = Position.Y;
            _deck.Update(c => c.Settings.Window = new WindowPosition { X = left, Y = top }, notify: false);
        };
        PositionChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _savePositionTimer.Stop();
            _savePositionTimer.Start();
        };

        Opened += OnOpened;
        Closing += OnClosing;
        Rebuild();
    }

    private int PanelWidthPx => (int)Math.Round(SettingsPanel.PanelWidth * DesktopScaling);

    // ───────────────────────── window setup ─────────────────────────

    private void OnOpened(object? sender, EventArgs e)
    {
        // Windows: clicking the deck never steals focus, so key combos go to the app the user
        // is working in. Turned off while the side panel is open (it has text boxes).
        FocusService.SetNoActivate(this, true);
        ReportHotkeyFailures();
        KeepOnScreen();
    }

    /// <summary>Places the window where it was (or bottom right the first time). Call before Show().</summary>
    public void RestorePosition()
    {
        var pos = _deck.Config.Settings.Window;
        if (pos != null)
        {
            Position = new PixelPoint((int)pos.X, (int)pos.Y);
            return;
        }
        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            var size = PixelSize.FromSize(DesiredOrMeasuredSize(), screen.Scaling);
            Position = new PixelPoint(area.Right - size.Width - 24, area.Bottom - size.Height - 24);
        }
    }

    private Size DesiredOrMeasuredSize()
    {
        Measure(Size.Infinity);
        return DesiredSize;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        if (_dialogOpen) return;
        _dialogOpen = true;
        bool yes;
        try
        {
            yes = await Dialogs.ConfirmAsync(this, L.T("Ukončiť Stream Deck", "Quit Stream Deck"),
                AppInfo.IsMac
                    ? L.T("Naozaj ukončiť Virtual Stream Deck?\n\nKlávesové skratky aj ovládanie z mobilu prestanú fungovať. Ak chceš okno len schovať, klikni na ikonu v lište s menu hore.",
                        "Quit Virtual Stream Deck?\n\nHotkeys and phone control will stop working. To just hide the window, click the icon in the menu bar.")
                    : L.T("Naozaj ukončiť Virtual Stream Deck?\n\nKlávesové skratky aj ovládanie z mobilu prestanú fungovať. Ak chceš okno len schovať, minimalizuj ho alebo klikni na ikonu v lište vedľa hodín.",
                        "Quit Virtual Stream Deck?\n\nHotkeys and phone control will stop working. To just hide the window, minimize it or click the icon in the tray next to the clock."));
        }
        finally
        {
            _dialogOpen = false;
        }
        if (yes) (Application.Current as App)?.ExitApp();
    }

    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    public void ShowDeck()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        KeepOnScreen();
    }

    public void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else ShowDeck();
    }

    /// <summary>Keeps the window reachable (e.g. after the grid grows or a monitor is unplugged).</summary>
    private void KeepOnScreen(bool whole = false)
    {
        if (WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(Bounds.Size, DesktopScaling);
        int visibleW = whole ? size.Width : Math.Min(size.Width, 120);
        int visibleH = whole ? size.Height : 40;
        int x = Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - visibleW));
        int y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - visibleH));
        if (x != Position.X || y != Position.Y) Position = new PixelPoint(x, y);
    }

    // ───────────────────────── rail + side panel ─────────────────────────

    private void BuildRail()
    {
        _rail.Children.Clear();
        _profilesRail = Ui.Clickable(Icons.Make("layers", 18, Ui.Muted, 1.8), () => ToggleSection(SettingsPanel.Profiles), 38, 38, L.T("Profily", "Profiles"));
        _settingsRail = Ui.Clickable(Icons.Make("sliders", 18, Ui.Muted, 1.8), () =>
        {
            if (_panel.IsVisible && IsSettingsSection(_panel.CurrentSection)) _lastSettingsSection = _panel.CurrentSection;
            ToggleSection(_lastSettingsSection, SettingsPanel.Layout, SettingsPanel.General, SettingsPanel.Phone, SettingsPanel.Obs);
        }, 38, 38, L.T("Nastavenia", "Settings"));
        _guideRail = Ui.Clickable(Icons.Make("book", 18, Ui.Muted, 1.8), () => ToggleSection(SettingsPanel.Guide), 38, 38, L.T("Návod", "Guide"));
        _updateRail = Ui.Clickable(Icons.Make("download", 18, Ui.Ok, 2), async () => await _updates.ConfirmAndInstallAsync(this), 38, 38);
        _updateRail.IsVisible = false;
        _rail.Children.AddRange(new Control[] { _profilesRail, _settingsRail, _guideRail, _updateRail });
        MarkRail();
    }

    private static bool IsSettingsSection(string s) =>
        s is SettingsPanel.Layout or SettingsPanel.General or SettingsPanel.Phone or SettingsPanel.Obs;

    /// <summary>Opens the side panel at a section, or collapses it (null).</summary>
    private void SetPanel(string? section)
    {
        bool open = section != null;
        bool wasOpen = _panel.IsVisible;
        bool normal = WindowState == WindowState.Normal && IsVisible;
        if (open)
        {
            _panel.ShowSection(section!);
            if (!wasOpen)
            {
                // The panel opens to the left of the deck: move the window so the deck stays where it is.
                if (normal) Position = new PixelPoint(Position.X - PanelWidthPx, Position.Y);
                _panel.IsVisible = true;
            }
            FocusService.SetNoActivate(this, false);
            Activate();
            Dispatcher.UIThread.Post(() => KeepOnScreen(whole: true), DispatcherPriority.Background);
        }
        else
        {
            _panel.IsVisible = false;
            if (wasOpen && normal) Position = new PixelPoint(Position.X + PanelWidthPx, Position.Y);
            FocusService.SetNoActivate(this, true);
        }
        MarkRail();
    }

    private void MarkRail()
    {
        var current = _panel.IsVisible ? _panel.CurrentSection : null;
        Ui.SetActive(_profilesRail, current == SettingsPanel.Profiles);
        Ui.SetActive(_guideRail, current == SettingsPanel.Guide);
        Ui.SetActive(_settingsRail, current != null && IsSettingsSection(current));
    }

    /// <summary>Rail button: open the panel at that section, or collapse it if it's already showing.</summary>
    private void ToggleSection(string section, params string[] alsoMine)
    {
        bool showingMine = _panel.IsVisible && (_panel.CurrentSection == section || alsoMine.Contains(_panel.CurrentSection));
        SetPanel(showingMine ? null : section);
    }

    /// <summary>Tray / menu "Settings…": show the deck with the side panel open.</summary>
    public void OpenSettings(string? section = null)
    {
        ShowDeck();
        SetPanel(section ?? _lastSettingsSection);
    }

    // ───────────────────────── updates ─────────────────────────

    private void OnUpdateChanged()
    {
        var u = _updates;
        var info = u.Available;
        bool busy = u.Current is UpdateService.State.Downloading or UpdateService.State.Restarting;
        _updateRail.IsVisible = info != null;
        _updateRail.IsEnabled = !busy;
        ToolTip.SetTip(_updateRail, u.Current switch
        {
            UpdateService.State.Downloading => L.T($"Sťahujem verziu {info?.Version}… {u.Percent} %", $"Downloading version {info?.Version}… {u.Percent} %"),
            UpdateService.State.Restarting => L.T("Spúšťam novú verziu…", "Starting the new version…"),
            _ => L.T($"Nová verzia {info?.Version} – klikni a aktualizuj", $"New version {info?.Version} – click to update"),
        });

        switch (u.Current)
        {
            case UpdateService.State.Available when info != null && info.Version != _announcedVersion:
                _announcedVersion = info.Version;
                ShowStatus(L.T($"Je dostupná nová verzia {info.Version}. Aktualizuješ ju zelenou šípkou v pruhu s ikonami.",
                    $"Version {info.Version} is available. Update with the green arrow in the icon bar."), isError: false);
                break;
            case UpdateService.State.Downloading when u.Percent / 10 != _lastToastPercent:
                _lastToastPercent = u.Percent / 10; // a toast every 10 %, not on every chunk
                ShowStatus(L.T($"Sťahujem verziu {info?.Version}… {u.Percent} %", $"Downloading version {info?.Version}… {u.Percent} %"), isError: false);
                break;
            case UpdateService.State.Restarting:
                ShowStatus(L.T("Spúšťam novú verziu…", "Starting the new version…"), isError: false);
                break;
            case UpdateService.State.Error when u.Error != null && !_panel.IsVisible:
                ShowStatus(u.Error, isError: true);
                break;
        }
        if (u.Current != UpdateService.State.Downloading) _lastToastPercent = -1;
    }

    // ───────────────────────── grid ─────────────────────────

    private void Rebuild()
    {
        var config = _deck.Config;
        var s = config.Settings;
        int rows = config.Rows, columns = config.Columns;

        Topmost = s.AlwaysOnTop;
        Title = $"Stream Deck – {config.ActiveProfile.Name}";
        _grid.Rows = rows;
        _grid.Columns = columns;
        _grid.Children.Clear();
        _tiles.Clear();

        _hotkeys.Apply(config);

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                var button = config.GetAt(r, c);
                var tile = CreateTile(r, c, button);
                _grid.Children.Add(tile);
                if (button != null) _tiles[button.Id] = tile;
            }
        }
    }

    private sealed record TileInfo(int Row, int Col, string? ButtonId);

    private Border CreateTile(int row, int col, DeckButton? button)
    {
        var s = _deck.Config.Settings;
        double size = s.ButtonSize;
        var info = new TileInfo(row, col, button?.Id);

        var tile = new Border
        {
            Width = size,
            Height = size,
            Margin = new Thickness(s.Spacing / 2.0),
            CornerRadius = new CornerRadius(Math.Round(size * 0.16)),
            Background = button == null ? Ui.EmptyTile : Ui.Tile,
            BorderThickness = new Thickness(1.5),
            BorderBrush = Ui.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = info,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = TransformOperations.Identity,
            Transitions = new Transitions
            {
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(110) },
            },
        };
        ToolTip.SetTip(tile, BuildToolTip(button));
        DragDrop.SetAllowDrop(tile, true);

        Control? emptyGlyph = null;
        if (button == null)
        {
            emptyGlyph = Icons.Make("plus", size * 0.28, Ui.EmptyGlyph);
            emptyGlyph.Opacity = 0;
            tile.Child = emptyGlyph;
        }
        else
        {
            tile.Child = CreateButtonContent(button, size, s.ShowLabels);
        }

        tile.PointerEntered += (_, _) =>
        {
            tile.Background = Ui.TileHover;
            if (emptyGlyph != null) emptyGlyph.Opacity = 1;
        };
        tile.PointerExited += (_, _) =>
        {
            tile.Background = button == null ? Ui.EmptyTile : Ui.Tile;
            if (emptyGlyph != null) emptyGlyph.Opacity = 0;
        };

        tile.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(tile);
            if (point.Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                return;
            }
            if (!point.Properties.IsLeftButtonPressed) return;
            _pressedTile = tile;
            _pressPoint = e.GetPosition(this);
            e.Pointer.Capture(tile);
            tile.RenderTransform = TransformOperations.Parse("scale(0.94)");
            e.Handled = true;
        };

        tile.PointerMoved += async (_, e) =>
        {
            if (_pressedTile != tile || info.ButtonId == null) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPoint.X) < 8 && Math.Abs(p.Y - _pressPoint.Y) < 8) return;

            // Start dragging the button to another cell.
            _pressedTile = null;
            e.Pointer.Capture(null);
            tile.RenderTransform = TransformOperations.Identity;
            tile.Opacity = 0.5;
            var data = new DataObject();
            data.Set(InternalDragFormat, info.ButtonId);
            try
            {
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            }
            finally
            {
                tile.Opacity = 1;
            }
        };

        tile.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right)
            {
                e.Handled = true;
                OpenEditor(info.Row, info.Col);
                return;
            }
            if (_pressedTile != tile) return;
            _pressedTile = null;
            e.Pointer.Capture(null);
            tile.RenderTransform = TransformOperations.Identity;
            e.Handled = true;

            var p = e.GetPosition(tile);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X <= tile.Bounds.Width && p.Y <= tile.Bounds.Height;
            if (!inside) return;

            if (info.ButtonId != null) Press(info.ButtonId);
            else OpenEditor(info.Row, info.Col);
        };

        tile.AddHandler(DragDrop.DragEnterEvent, (_, e) => OnTileDragOver(tile, e));
        tile.AddHandler(DragDrop.DragOverEvent, (_, e) => OnTileDragOver(tile, e));
        tile.AddHandler(DragDrop.DragLeaveEvent, (_, _) => tile.BorderBrush = Ui.Transparent);
        tile.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            tile.BorderBrush = Ui.Transparent;
            OnTileDrop(info, e);
        });

        return tile;
    }

    private Control CreateButtonContent(DeckButton b, double size, bool showLabels)
    {
        bool hasText = !string.IsNullOrWhiteSpace(b.Label);
        bool labelBelow = showLabels && hasText;
        var icon = _icons.Load(b.Icon);

        var grid = new Grid();
        if (labelBelow) grid.RowDefinitions = new RowDefinitions("*,Auto");

        Control visual;
        if (icon != null)
        {
            double iconSize = size * (labelBelow ? 0.46 : 0.6);
            var image = new Image { Source = icon, Width = iconSize, Height = iconSize, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            visual = image;
        }
        else if (hasText && !showLabels)
        {
            // No icon and labels are off: the label itself is the most useful thing to show.
            visual = new TextBlock
            {
                Text = b.Label,
                Foreground = Ui.Label,
                FontSize = Math.Max(10, size * 0.15),
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6),
            };
        }
        else
        {
            visual = Icons.Make(DefaultIcon(b.Action), size * (labelBelow ? 0.3 : 0.36), Ui.Glyph);
        }

        visual.HorizontalAlignment = HorizontalAlignment.Center;
        visual.VerticalAlignment = VerticalAlignment.Center;
        if (labelBelow) visual.Margin = new Thickness(0, size * 0.1, 0, 0);
        grid.Children.Add(visual);

        if (labelBelow)
        {
            var label = new TextBlock
            {
                Text = b.Label,
                Foreground = Ui.Label,
                FontSize = Math.Max(9, size * 0.135),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 0, 4, size * 0.09),
            };
            Grid.SetRow(label, 1);
            grid.Children.Add(label);
        }
        return grid;
    }

    private static string DefaultIcon(DeckAction? action) => action switch
    {
        OpenAction o when o.IsUrl => "globe",
        OpenAction o when Directory.Exists(Expand(o.Target)) && !o.Target.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase) => "folder",
        OpenAction o when IsProgram(o.Target) => "app",
        OpenAction => "file",
        KeysAction => "keyboard",
        MediaAction m => m.Command switch
        {
            MediaCommand.PlayPause => "playpause",
            MediaCommand.Next => "next",
            MediaCommand.Prev => "prev",
            MediaCommand.Stop => "stop",
            MediaCommand.VolumeUp => "volup",
            MediaCommand.VolumeDown => "voldown",
            MediaCommand.Mute => "mute",
            _ => "play",
        },
        ProfileAction => "layers",
        ObsAction o => o.Command switch
        {
            ObsCommand.SwitchScene => "monitor",
            ObsCommand.ToggleRecord => "record",
            ObsCommand.ToggleStream => "stream",
            ObsCommand.ToggleMute => "mic",
            ObsCommand.ToggleSource => "eye",
            ObsCommand.SaveReplay => "save",
            _ => "video",
        },
        _ => "plus",
    };

    private static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    private static bool IsProgram(string target)
    {
        var t = Expand(target).TrimEnd('/');
        var ext = Path.GetExtension(t).ToLowerInvariant();
        return ext is ".exe" or ".lnk" or ".bat" or ".cmd" or ".appref-ms" or ".app" || ext.Length == 0;
    }

    private string BuildToolTip(DeckButton? b)
    {
        if (b == null) return L.T("Klikni alebo sem pretiahni program, súbor či odkaz", "Click, or drop a program, file or link here");

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(b.Label)) lines.Add(b.Label!);
        lines.Add(b.Action is ProfileAction pa
            ? L.T("Prepnúť na profil ", "Switch to profile ") + Quote(_deck.Config.FindProfile(pa.ProfileId)?.Name ?? "?")
            : DescribeAction(b.Action));
        if (Hotkey.TryParse(b.Hotkey, out var hk))
        {
            var name = HotkeyBox.Describe(hk);
            lines.Add(_hotkeys.Failed.Contains(b.Id)
                ? L.T($"⚠ Skratku {name} používa iná aplikácia", $"⚠ {name} is used by another app")
                : L.T($"Skratka: {name}", $"Hotkey: {name}"));
        }
        lines.Add(L.T("Pravý klik = upraviť, ťahaním presunieš", "Right-click = edit, drag to move"));
        return string.Join("\n", lines);
    }

    internal static string DescribeAction(DeckAction? action) => action switch
    {
        OpenAction o => L.T("Otvoriť: ", "Open: ") + o.Target,
        KeysAction k => L.T("Poslať skratku: ", "Send keys: ") + (Hotkey.TryParse(k.Keys, out var hk) ? HotkeyBox.Describe(hk) : k.Keys),
        ProfileAction => L.T("Prepnúť profil", "Switch profile"),
        MediaAction m => MediaName(m.Command),
        ObsAction o => "OBS: " + ObsName(o.Command) + o.Command switch
        {
            ObsCommand.SwitchScene => " " + Quote(o.Scene),
            ObsCommand.ToggleMute => " " + Quote(o.Source),
            ObsCommand.ToggleSource => " " + Quote(o.Source) +
                                       (string.IsNullOrWhiteSpace(o.Scene) ? "" : L.T(" v scéne ", " in scene ") + Quote(o.Scene)),
            _ => "",
        },
        _ => L.T("Bez akcie", "No action"),
    };

    /// <summary>„Slovak“ or "English" quotes.</summary>
    internal static string Quote(string? text) => L.T($"„{text}“", $"\"{text}\"");

    internal static string ObsName(ObsCommand c) => c switch
    {
        ObsCommand.SwitchScene => L.T("Prepnúť scénu", "Switch scene"),
        ObsCommand.ToggleRecord => L.T("Zapnúť / vypnúť nahrávanie", "Start / stop recording"),
        ObsCommand.ToggleStream => L.T("Zapnúť / vypnúť stream", "Start / stop streaming"),
        ObsCommand.ToggleMute => L.T("Stlmiť / zapnúť zvuk zdroja", "Mute / unmute a source"),
        ObsCommand.ToggleSource => L.T("Zobraziť / skryť zdroj", "Show / hide a source"),
        ObsCommand.SaveReplay => L.T("Uložiť replay", "Save replay"),
        _ => c.ToString(),
    };

    internal static string MediaName(MediaCommand c) => c switch
    {
        MediaCommand.PlayPause => L.T("Play / pauza", "Play / pause"),
        MediaCommand.Next => L.T("Ďalšia skladba", "Next track"),
        MediaCommand.Prev => L.T("Predošlá skladba", "Previous track"),
        MediaCommand.Stop => "Stop",
        MediaCommand.VolumeUp => L.T("Hlasitosť +", "Volume up"),
        MediaCommand.VolumeDown => L.T("Hlasitosť −", "Volume down"),
        MediaCommand.Mute => L.T("Stlmiť / zapnúť zvuk", "Mute / unmute"),
        _ => c.ToString(),
    };

    // ───────────────────────── pressing ─────────────────────────

    private async void Press(string buttonId)
    {
        _tiles.TryGetValue(buttonId, out var tile);
        if (tile != null) Pulse(tile);

        var result = await _deck.PressAsync(buttonId);
        if (result.Success) return;

        if (tile != null) FlashBorder(tile, Ui.Error);
        ShowStatus(result.Error ?? L.T("Akcia zlyhala.", "The action failed."), isError: true);
    }

    /// <summary>Called when a phone presses a button, so you can see it happen on the computer too.</summary>
    public void FlashButton(string buttonId)
    {
        if (_tiles.TryGetValue(buttonId, out var tile)) Pulse(tile);
    }

    private static async void Pulse(Border tile)
    {
        tile.RenderTransform = TransformOperations.Parse("scale(0.92)");
        FlashBorder(tile, Ui.Accent, 180);
        await Task.Delay(110);
        tile.RenderTransform = TransformOperations.Identity;
    }

    private static async void FlashBorder(Border tile, IBrush brush, int ms = 1500)
    {
        tile.BorderBrush = brush;
        await Task.Delay(ms);
        if (ReferenceEquals(tile.BorderBrush, brush)) tile.BorderBrush = Ui.Transparent;
    }

    /// <summary>Small toast at the bottom of the deck.</summary>
    private async void ShowStatus(string text, bool isError)
    {
        int version = ++_statusVersion;
        _toastText.Text = text;
        _toastText.Foreground = isError ? Ui.ErrorText : Ui.Label;
        _toast.BorderBrush = isError ? Ui.Error : Ui.Accent;
        _toast.IsVisible = true;
        await Task.Delay(isError ? 6000 : 3500);
        if (version != _statusVersion) return;
        _toast.IsVisible = false;
    }

    private void ReportHotkeyFailures()
    {
        if (_hotkeys.Failed.Count == 0) return;
        var names = new List<string>();
        foreach (var id in _hotkeys.Failed)
        {
            var b = _deck.Config.FindById(id);
            if (Hotkey.TryParse(b?.Hotkey, out var hk)) names.Add(HotkeyBox.Describe(hk));
        }
        ShowStatus(L.T("Obsadené skratky: ", "Hotkeys already taken: ") + string.Join(", ", names), isError: true);
    }

    // ───────────────────────── drag & drop ─────────────────────────

    private static void OnTileDragOver(Border tile, DragEventArgs e)
    {
        e.DragEffects = GetDropEffect(e.Data);
        tile.BorderBrush = e.DragEffects == DragDropEffects.None ? Ui.Transparent : Ui.Accent;
        e.Handled = true;
    }

    private static DragDropEffects GetDropEffect(IDataObject data)
    {
        if (data.Contains(InternalDragFormat)) return DragDropEffects.Move;
        if (data.Contains(DataFormats.Files)) return DragDropEffects.Copy;
        if (TryGetUrl(data, out _)) return DragDropEffects.Link;
        return DragDropEffects.None;
    }

    private static bool TryGetUrl(IDataObject data, out string url)
    {
        url = "";
        var text = data.Contains(DataFormats.Text) ? data.GetText()?.Trim() : null;
        if (string.IsNullOrEmpty(text) || text.Contains('\n')) return false;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            url = text;
            return true;
        }
        return false;
    }

    private void OnTileDrop(TileInfo info, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.Contains(InternalDragFormat))
        {
            if (e.Data.Get(InternalDragFormat) is string id) _deck.Update(c => c.Move(id, info.Row, info.Col));
            return;
        }

        var targets = new List<string>();
        if (e.Data.Contains(DataFormats.Files))
            targets.AddRange(e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? Enumerable.Empty<string>());
        else if (TryGetUrl(e.Data, out var url)) targets.Add(url);
        if (targets.Count == 0) return;

        int skipped = 0;
        var buttons = targets.Select(t => (Target: t, Button: CreateButtonFor(t))).ToList();
        _deck.Update(c =>
        {
            foreach (var (_, b) in buttons)
            {
                // Never overwrite: use this cell if it's free, otherwise the next free one.
                var cell = c.FindEmptyCell(info.Row, info.Col);
                if (cell == null)
                {
                    skipped++;
                    continue;
                }
                b.Row = cell.Value.Row;
                b.Col = cell.Value.Col;
                c.Upsert(b);
            }
        });

        if (skipped > 0)
            ShowStatus(L.T($"Nezmestilo sa: {skipped}. Pridaj riadok alebo stĺpec v nastaveniach.",
                $"Did not fit: {skipped}. Add a row or column in the settings."), isError: true);
    }

    private DeckButton CreateButtonFor(string target)
    {
        bool isPath = File.Exists(target) || Directory.Exists(target);
        return new DeckButton
        {
            Label = isPath ? LabelForPath(target) : LabelForUrl(target),
            Icon = isPath ? _icons.CreateFrom(target) : null,
            Action = new OpenAction { Target = target },
        };
    }

    internal static string LabelForPath(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Directory.Exists(path) && !trimmed.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(trimmed)
            : Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    internal static string LabelForUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    // ───────────────────────── editor ─────────────────────────

    private async void OpenEditor(int row, int col)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        _hotkeys.Suspend(); // so the user can record a hotkey that is currently registered
        string? savedId = null;
        try
        {
            var existing = _deck.Config.GetAt(row, col);
            var dialog = new ButtonEditorWindow(_deck.Config, existing, row, col, _icons, _obs);
            bool ok = await dialog.ShowDialog<bool>(this);
            if (ok)
            {
                if (dialog.DeleteRequested && existing != null)
                {
                    _deck.Update(c => c.Remove(existing.Id));
                }
                else if (dialog.Result is { } result)
                {
                    savedId = result.Id;
                    _deck.Update(c => c.Upsert(result));
                }
            }
        }
        finally
        {
            _hotkeys.Resume();
            _dialogOpen = false;
        }

        if (savedId != null && _hotkeys.Failed.Contains(savedId))
        {
            Rebuild(); // refresh the tooltip warning
            var hk = _deck.Config.FindById(savedId)?.Hotkey;
            var name = Hotkey.TryParse(hk, out var parsed) ? HotkeyBox.Describe(parsed) : hk;
            ShowStatus(L.T($"Skratku {name} už používa iná aplikácia. Vyber inú.", $"{name} is already used by another app. Pick a different one."), isError: true);
        }
    }
}
