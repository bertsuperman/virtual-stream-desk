using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;

namespace VirtualStreamDeck.App;

public partial class MainWindow : Window
{
    private const string InternalDragFormat = "VirtualStreamDeck.ButtonId";

    private static readonly Brush TileBrush = Frozen(0x26, 0x26, 0x2C);
    private static readonly Brush TileHoverBrush = Frozen(0x31, 0x31, 0x39);
    private static readonly Brush EmptyBrush = Frozen(0x1D, 0x1D, 0x21);
    private static readonly Brush LabelBrush = Frozen(0xDA, 0xDA, 0xE0);
    private static readonly Brush GlyphBrush = Frozen(0xB4, 0xB4, 0xBF);
    private static readonly Brush EmptyGlyphBrush = Frozen(0x5A, 0x5A, 0x63);
    private static readonly Brush AccentBrush = Frozen(0x4C, 0x8D, 0xFF);
    private static readonly Brush ErrorBrush = Frozen(0xE5, 0x48, 0x4D);
    private static readonly Brush HeaderTextBrush = Frozen(0x5F, 0x5F, 0x68);

    private readonly Deck _deck;
    private readonly ObsClient _obs;
    private readonly IconService _icons;
    private readonly Dictionary<string, Border> _tiles = new();
    private HotkeyService? _hotkeys;
    private bool _dialogOpen;
    private bool _exiting;

    // Tile press / drag state
    private Border? _pressedTile;
    private Point _pressPoint;

    // Window drag state (manual, because a non-activating window does not drag well with DragMove)
    private bool _movingWindow;
    private Win32.POINT _moveStartCursor;
    private double _moveStartLeft, _moveStartTop;

    private int _statusVersion;

    internal MainWindow(Deck deck, ObsClient obs)
    {
        InitializeComponent();
        _deck = deck;
        _obs = obs;
        _icons = new IconService(deck.Store);

        var pos = deck.Config.Settings.Window;
        if (pos != null && IsOnScreen(pos.X, pos.Y))
        {
            Left = pos.X;
            Top = pos.Y;
        }

        deck.Changed += (_, _) => Rebuild();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => KeepOnScreen();
        Rebuild();
    }

    // ───────────────────────── window setup ─────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // WS_EX_NOACTIVATE: clicking the deck never steals focus, so key combos
        // go to the app the user is working in. (Not a tool window: with ShowInTaskbar=False
        // WPF gives it a hidden owner, which already keeps it out of the taskbar and Alt+Tab,
        // and it can still get a real taskbar button while minimized.)
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | Win32.WS_EX_NOACTIVATE));

        _hotkeys = new HotkeyService(this);
        _hotkeys.Pressed += Press;
        _hotkeys.Apply(_deck.Config);
        ReportHotkeyFailures();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_deck.Config.Settings.Window == null)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public void CloseForExit()
    {
        _exiting = true;
        _hotkeys?.Dispose();
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

    /// <summary>Real minimize: the deck gets a taskbar button only while it is minimized.</summary>
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized && ShowInTaskbar)
        {
            ShowInTaskbar = false;
            KeepOnScreen();
        }
    }

    // ───────────────────────── grid ─────────────────────────

    private void Rebuild()
    {
        var config = _deck.Config;
        var s = config.Settings;

        Topmost = s.AlwaysOnTop;
        DeckGrid.Rows = s.Rows;
        DeckGrid.Columns = s.Columns;
        DeckGrid.Children.Clear();
        _tiles.Clear();

        _hotkeys?.Apply(config);

        for (int r = 0; r < s.Rows; r++)
        {
            for (int c = 0; c < s.Columns; c++)
            {
                var button = config.GetAt(r, c);
                var tile = CreateTile(r, c, button);
                DeckGrid.Children.Add(tile);
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
            Background = button == null ? EmptyBrush : TileBrush,
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            AllowDrop = true,
            Cursor = Cursors.Hand,
            Tag = info,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            ToolTip = BuildToolTip(button),
        };

        UIElement content;
        TextBlock? emptyGlyph = null;
        if (button == null)
        {
            emptyGlyph = Glyph("\uE710", size * 0.28, EmptyGlyphBrush);
            emptyGlyph.Opacity = 0;
            content = emptyGlyph;
        }
        else
        {
            content = CreateButtonContent(button, size, s.ShowLabels);
        }
        tile.Child = content;

        tile.MouseEnter += (_, _) =>
        {
            tile.Background = TileHoverBrush;
            if (emptyGlyph != null) emptyGlyph.Opacity = 1;
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.Background = button == null ? EmptyBrush : TileBrush;
            if (emptyGlyph != null) emptyGlyph.Opacity = 0;
        };

        tile.MouseLeftButtonDown += (_, e) =>
        {
            _pressedTile = tile;
            _pressPoint = e.GetPosition(this);
            tile.CaptureMouse();
            SetScale(tile, 0.94);
            e.Handled = true;
        };

        tile.MouseMove += (_, e) =>
        {
            if (_pressedTile != tile || e.LeftButton != MouseButtonState.Pressed || info.ButtonId == null) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
                Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;

            // Start dragging the button to another cell.
            _pressedTile = null;
            tile.ReleaseMouseCapture();
            SetScale(tile, 1);
            tile.Opacity = 0.5;
            DragDrop.DoDragDrop(tile, new DataObject(InternalDragFormat, info.ButtonId), DragDropEffects.Move);
            tile.Opacity = 1;
        };

        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressedTile != tile) return;
            _pressedTile = null;
            tile.ReleaseMouseCapture();
            SetScale(tile, 1);
            e.Handled = true;

            var p = e.GetPosition(tile);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X <= tile.ActualWidth && p.Y <= tile.ActualHeight;
            if (!inside) return;

            if (info.ButtonId != null) Press(info.ButtonId);
            else OpenEditor(info.Row, info.Col);
        };

        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenEditor(info.Row, info.Col);
        };

        tile.DragEnter += (_, e) => OnTileDragOver(tile, e);
        tile.DragOver += (_, e) => OnTileDragOver(tile, e);
        tile.DragLeave += (_, _) => tile.BorderBrush = Brushes.Transparent;
        tile.Drop += (_, e) =>
        {
            tile.BorderBrush = Brushes.Transparent;
            OnTileDrop(info, e);
        };

        return tile;
    }

    private UIElement CreateButtonContent(DeckButton b, double size, bool showLabels)
    {
        bool hasText = !string.IsNullOrWhiteSpace(b.Label);
        bool labelBelow = showLabels && hasText;
        var icon = _icons.Load(b.Icon);

        var grid = new Grid();
        if (labelBelow)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        FrameworkElement visual;
        if (icon != null)
        {
            double iconSize = size * (labelBelow ? 0.46 : 0.6);
            var image = new Image { Source = icon, Width = iconSize, Height = iconSize, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            visual = image;
        }
        else if (hasText && !showLabels)
        {
            // No icon and labels are off: the label itself is the most useful thing to show.
            visual = new TextBlock
            {
                Text = b.Label,
                Foreground = LabelBrush,
                FontSize = Math.Max(10, size * 0.15),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6),
            };
        }
        else
        {
            visual = Glyph(DefaultGlyph(b.Action), size * (labelBelow ? 0.3 : 0.38), GlyphBrush);
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
                Foreground = LabelBrush,
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

    private TextBlock Glyph(string glyph, double fontSize, Brush brush) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)FindResource("IconFont"),
        FontSize = fontSize,
        Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static string DefaultGlyph(DeckAction? action) => action switch
    {
        OpenAction o when o.IsUrl => "\uE774",                                    // Globe
        OpenAction o when Directory.Exists(Expand(o.Target)) => "\uE8B7",         // Folder
        OpenAction o when IsProgram(o.Target) => "\uE71D",                        // App
        OpenAction => "\uE8A5",                                                   // Document
        KeysAction => "\uE765",                                                   // Keyboard
        MediaAction m => m.Command switch
        {
            MediaCommand.PlayPause => "\uE768",
            MediaCommand.Next => "\uE893",
            MediaCommand.Prev => "\uE892",
            MediaCommand.Stop => "\uE71A",
            MediaCommand.VolumeUp => "\uE995",
            MediaCommand.VolumeDown => "\uE993",
            MediaCommand.Mute => "\uE74F",
            _ => "\uE767",
        },
        ObsAction o => o.Command switch
        {
            ObsCommand.SwitchScene => "\uE7F4",   // TV monitor
            ObsCommand.ToggleRecord => "\uE7C8",  // Record
            ObsCommand.ToggleStream => "\uE93E",  // Streaming
            ObsCommand.ToggleMute => "\uE720",    // Microphone
            ObsCommand.ToggleSource => "\uE7B3",  // Eye
            ObsCommand.SaveReplay => "\uE74E",    // Save
            _ => "\uE714",
        },
        _ => "\uE710",
    };

    private static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    private static bool IsProgram(string target)
    {
        var ext = Path.GetExtension(Expand(target)).ToLowerInvariant();
        return ext is ".exe" or ".lnk" or ".bat" or ".cmd" or ".appref-ms" || ext.Length == 0;
    }

    private string BuildToolTip(DeckButton? b)
    {
        if (b == null) return "Klikni alebo sem pretiahni program, súbor či odkaz";

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(b.Label)) lines.Add(b.Label!);
        lines.Add(DescribeAction(b.Action));
        if (!string.IsNullOrWhiteSpace(b.Hotkey))
        {
            bool failed = _hotkeys?.Failed.Contains(b.Id) == true;
            lines.Add(failed ? $"⚠ Skratku {b.Hotkey} používa iná aplikácia" : $"Skratka: {b.Hotkey}");
        }
        lines.Add("Pravý klik = upraviť, ťahaním presunieš");
        return string.Join("\n", lines);
    }

    internal static string DescribeAction(DeckAction? action) => action switch
    {
        OpenAction o => "Otvoriť: " + o.Target,
        KeysAction k => "Poslať skratku: " + k.Keys,
        MediaAction m => MediaName(m.Command),
        ObsAction o => "OBS: " + ObsName(o.Command) + o.Command switch
        {
            ObsCommand.SwitchScene => $" „{o.Scene}“",
            ObsCommand.ToggleMute => $" „{o.Source}“",
            ObsCommand.ToggleSource => $" „{o.Source}“" + (string.IsNullOrWhiteSpace(o.Scene) ? "" : $" v scéne „{o.Scene}“"),
            _ => "",
        },
        _ => "Bez akcie",
    };

    internal static string ObsName(ObsCommand c) => c switch
    {
        ObsCommand.SwitchScene => "Prepnúť scénu",
        ObsCommand.ToggleRecord => "Zapnúť / vypnúť nahrávanie",
        ObsCommand.ToggleStream => "Zapnúť / vypnúť stream",
        ObsCommand.ToggleMute => "Stlmiť / zapnúť zvuk zdroja",
        ObsCommand.ToggleSource => "Zobraziť / skryť zdroj",
        ObsCommand.SaveReplay => "Uložiť replay",
        _ => c.ToString(),
    };

    internal static string MediaName(MediaCommand c) => c switch
    {
        MediaCommand.PlayPause => "Play / pauza",
        MediaCommand.Next => "Ďalšia skladba",
        MediaCommand.Prev => "Predošlá skladba",
        MediaCommand.Stop => "Stop",
        MediaCommand.VolumeUp => "Hlasitosť +",
        MediaCommand.VolumeDown => "Hlasitosť −",
        MediaCommand.Mute => "Stlmiť / zapnúť zvuk",
        _ => c.ToString(),
    };

    // ───────────────────────── pressing ─────────────────────────

    private async void Press(string buttonId)
    {
        _tiles.TryGetValue(buttonId, out var tile);
        if (tile != null) Pulse(tile);

        var result = await _deck.PressAsync(buttonId);
        if (result.Success) return;

        if (tile != null) FlashBorder(tile, ErrorBrush);
        ShowStatus(result.Error ?? "Akcia zlyhala.", isError: true);
    }

    private static void SetScale(Border tile, double scale)
    {
        if (tile.RenderTransform is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            st.ScaleX = st.ScaleY = scale;
        }
    }

    private static void Pulse(Border tile)
    {
        if (tile.RenderTransform is not ScaleTransform st) return;
        var anim = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        FlashBorder(tile, AccentBrush, 180);
    }

    private static async void FlashBorder(Border tile, Brush brush, int ms = 1500)
    {
        tile.BorderBrush = brush;
        await Task.Delay(ms);
        if (tile.BorderBrush == brush) tile.BorderBrush = Brushes.Transparent;
    }

    private async void ShowStatus(string text, bool isError)
    {
        int version = ++_statusVersion;
        HeaderText.Text = text;
        HeaderText.Foreground = isError ? ErrorBrush : AccentBrush;
        HeaderText.ToolTip = text;
        await Task.Delay(isError ? 6000 : 3500);
        if (version != _statusVersion) return;
        HeaderText.Text = "STREAM DECK";
        HeaderText.Foreground = HeaderTextBrush;
        HeaderText.ToolTip = null;
    }

    private void ReportHotkeyFailures()
    {
        if (_hotkeys == null || _hotkeys.Failed.Count == 0) return;
        var names = new List<string>();
        foreach (var id in _hotkeys.Failed)
        {
            var b = _deck.Config.FindById(id);
            if (b?.Hotkey != null) names.Add(b.Hotkey);
        }
        ShowStatus($"Obsadené skratky: {string.Join(", ", names)}", isError: true);
    }

    // ───────────────────────── drag & drop ─────────────────────────

    private static void OnTileDragOver(Border tile, DragEventArgs e)
    {
        e.Effects = GetDropEffect(e.Data);
        tile.BorderBrush = e.Effects == DragDropEffects.None ? Brushes.Transparent : AccentBrush;
        e.Handled = true;
    }

    private static DragDropEffects GetDropEffect(IDataObject data)
    {
        if (data.GetDataPresent(InternalDragFormat)) return DragDropEffects.Move;
        if (data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.Copy;
        if (TryGetUrl(data, out _)) return DragDropEffects.Link;
        return DragDropEffects.None;
    }

    private static bool TryGetUrl(IDataObject data, out string url)
    {
        url = "";
        string? text = null;
        if (data.GetDataPresent(DataFormats.UnicodeText)) text = data.GetData(DataFormats.UnicodeText) as string;
        else if (data.GetDataPresent(DataFormats.Text)) text = data.GetData(DataFormats.Text) as string;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains('\n')) return false;

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            url = text;
            return true;
        }
        return false;
    }

    private void OnTileDrop(TileInfo info, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetDataPresent(InternalDragFormat))
        {
            if (e.Data.GetData(InternalDragFormat) is string id)
                _deck.Update(c => c.Move(id, info.Row, info.Col));
            return;
        }

        var targets = new List<string>();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) targets.AddRange(files);
        else if (TryGetUrl(e.Data, out var url)) targets.Add(url);
        if (targets.Count == 0) return;

        int skipped = 0;
        _deck.Update(c =>
        {
            foreach (var target in targets)
            {
                // Never overwrite: use this cell if it's free, otherwise the next free one.
                var cell = c.FindEmptyCell(info.Row, info.Col);
                if (cell == null)
                {
                    skipped++;
                    continue;
                }
                c.Upsert(CreateButtonFor(target, cell.Value.Row, cell.Value.Col));
            }
        });

        if (skipped > 0)
            ShowStatus($"Nezmestilo sa: {skipped}. Pridaj riadok alebo stĺpec v nastaveniach.", isError: true);
    }

    private DeckButton CreateButtonFor(string target, int row, int col)
    {
        bool isPath = File.Exists(target) || Directory.Exists(target);
        return new DeckButton
        {
            Row = row,
            Col = col,
            Label = isPath ? LabelForPath(target) : LabelForUrl(target),
            Icon = isPath ? _icons.CreateFrom(target) : null,
            Action = new OpenAction { Target = target },
        };
    }

    internal static string LabelForPath(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Directory.Exists(path) ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    internal static string LabelForUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    // ───────────────────────── dialogs ─────────────────────────

    private void OpenEditor(int row, int col)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        _hotkeys?.Suspend(); // so the user can record a hotkey that is currently registered
        string? savedId = null;
        try
        {
            var existing = _deck.Config.GetAt(row, col);
            var dialog = new ButtonEditorWindow(_deck.Config, existing, row, col, _icons, _obs) { Owner = this };
            if (dialog.ShowDialog() == true)
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
            _hotkeys?.Resume();
            _dialogOpen = false;
        }

        if (savedId != null && _hotkeys?.Failed.Contains(savedId) == true)
        {
            Rebuild(); // refresh the tooltip warning
            var hk = _deck.Config.FindById(savedId)?.Hotkey;
            ShowStatus($"Skratku {hk} už používa iná aplikácia. Vyber inú.", isError: true);
        }
    }

    public void OpenSettings()
    {
        if (_dialogOpen) return;
        ShowDeck();
        _dialogOpen = true;
        try
        {
            new SettingsWindow(_deck, _obs) { Owner = this }.ShowDialog();
        }
        finally
        {
            _dialogOpen = false;
        }
        ReportHotkeyFailures();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    // ───────────────────────── moving the window ─────────────────────────

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Win32.GetCursorPos(out _moveStartCursor);
        _moveStartLeft = Left;
        _moveStartTop = Top;
        _movingWindow = true;
        Header.CaptureMouse();
        e.Handled = true;
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_movingWindow) return;
        Win32.GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _moveStartLeft + (p.X - _moveStartCursor.X) / dpi.DpiScaleX;
        Top = _moveStartTop + (p.Y - _moveStartCursor.Y) / dpi.DpiScaleY;
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_movingWindow) return;
        _movingWindow = false;
        Header.ReleaseMouseCapture();
        KeepOnScreen();
        double left = Left, top = Top;
        _deck.Update(c => c.Settings.Window = new WindowPosition { X = left, Y = top }, notify: false);
    }

    private static bool IsOnScreen(double x, double y) =>
        x >= SystemParameters.VirtualScreenLeft - 20 &&
        y >= SystemParameters.VirtualScreenTop - 20 &&
        x <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
        y <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    /// <summary>Keeps at least the header reachable, e.g. after the grid grows or a monitor is unplugged.</summary>
    private void KeepOnScreen()
    {
        if (!IsLoaded) return;
        double minX = SystemParameters.VirtualScreenLeft;
        double minY = SystemParameters.VirtualScreenTop;
        double maxX = minX + SystemParameters.VirtualScreenWidth - Math.Min(ActualWidth, 120);
        double maxY = minY + SystemParameters.VirtualScreenHeight - 40;
        Left = Math.Clamp(Left, minX, Math.Max(minX, maxX));
        Top = Math.Clamp(Top, minY, Math.Max(minY, maxY));
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
