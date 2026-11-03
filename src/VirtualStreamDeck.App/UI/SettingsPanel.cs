using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;
using VirtualStreamDeck.Core.Remote;

namespace VirtualStreamDeck.App.UI;

/// <summary>
/// Settings, profiles and the guide as a collapsible side panel on the left of the deck.
/// Every change is applied and saved immediately. Switching the language rebuilds the panel.
/// </summary>
internal sealed class SettingsPanel : Border
{
    public const string Profiles = "profiles", Layout = "layout", General = "general",
        Phone = "phone", Obs = "obs", Guide = "guide";

    public const double PanelWidth = 470;

    private readonly Deck _deck;
    private readonly ObsClient _obs;
    private readonly RemoteServer _remote;
    private readonly UpdateService _updates;
    private string _section = Profiles;
    private bool _loading;

    private readonly Dictionary<string, Control> _sections = new();
    private ListBox _nav = null!;

    // Profiles
    private ListBox _profileList = null!;
    private TextBox _profileName = null!;
    // Layout
    private TextBlock _gridTitle = null!, _hiddenInfo = null!;
    private Slider _rows = null!, _columns = null!, _size = null!, _spacing = null!;
    private CheckBox _labels = null!;
    // General
    private ComboBox _language = null!;
    private CheckBox _topmost = null!, _autostart = null!, _autoUpdate = null!;
    private Button _updateButton = null!;
    private TextBlock _updateStatus = null!;
    // Phone
    private CheckBox _remoteEnabled = null!;
    private StackPanel _remotePanel = null!;
    private Image _qr = null!;
    private ComboBox _address = null!;
    private TextBox _remotePort = null!, _url = null!;
    private TextBlock _remoteStatus = null!;
    // OBS
    private TextBox _obsHost = null!, _obsPort = null!, _obsPassword = null!;
    private TextBlock _obsStatus = null!;

    public SettingsPanel(Deck deck, ObsClient obs, RemoteServer remote, UpdateService updates)
    {
        _deck = deck;
        _obs = obs;
        _remote = remote;
        _updates = updates;
        Width = PanelWidth;
        Height = 580;
        Background = Ui.PanelBg;
        BorderBrush = Ui.Line;
        BorderThickness = new Thickness(0, 0, 1, 0);

        Build();

        // Changes from anywhere (header, phone, buttons) keep the panel in sync.
        _deck.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshFromDeck);
        _remote.ClientsChanged += () => Dispatcher.UIThread.Post(RefreshRemote);
        _updates.Changed += RefreshUpdate;
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            Build();
            ShowSection(_section);
        });
    }

    /// <summary>Raised when the user clicks the collapse arrow.</summary>
    public event Action? CollapseRequested;

    public string CurrentSection => _section;

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    // ───────────────────────── layout of the panel ─────────────────────────

    private void Build()
    {
        _loading = true;
        _sections.Clear();

        var header = new DockPanel { Margin = new Thickness(18, 12, 10, 10) };
        var collapse = Ui.Clickable(Icons.Make("chevronleft", 18, Ui.Muted), () => CollapseRequested?.Invoke(), 34, 30,
            L.T("Zbaliť panel", "Collapse panel"));
        DockPanel.SetDock(collapse, Dock.Right);
        header.Children.Add(collapse);
        header.Children.Add(new TextBlock
        {
            Text = L.T("Nastavenia", "Settings"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ui.Label,
            VerticalAlignment = VerticalAlignment.Center,
        });
        DockPanel.SetDock(header, Dock.Top);

        _nav = new ListBox
        {
            Width = 140,
            Margin = new Thickness(8, 0, 0, 12),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        foreach (var (key, icon, sk, en) in new[]
                 {
                     (Profiles, "layers", "Profily", "Profiles"),
                     (Layout, "grid", "Rozloženie", "Layout"),
                     (General, "sliders", "Všeobecné", "General"),
                     (Phone, "phone", "Mobil", "Phone"),
                     (Obs, "video", "OBS", "OBS"),
                     (Guide, "book", "Návod", "Guide"),
                 })
        {
            _nav.Items.Add(new ListBoxItem
            {
                Tag = key,
                Content = Ui.Row(10, Icons.Make(icon, 16, Ui.Glyph, 1.8), new TextBlock { Text = L.T(sk, en), VerticalAlignment = VerticalAlignment.Center }),
            });
        }
        _nav.SelectionChanged += (_, _) =>
        {
            if ((_nav.SelectedItem as ListBoxItem)?.Tag is string key && key != _section) ShowSection(key);
        };
        DockPanel.SetDock(_nav, Dock.Left);

        var footer = Ui.Hint(AppInfo.IsMac
            ? L.T("Kým je panel otvorený, tlačidlá so skratkou pošlú klávesy do appky, v ktorej si naposledy pracoval.",
                "While the panel is open, key buttons send the keys to the app you used last.")
            : L.T("Kým je panel otvorený, deck sa správa ako bežné okno (dá sa doň písať). Skratky z tlačidiel pôjdu správne do iných appiek, keď panel zbalíš.",
                "While the panel is open, the deck behaves like a normal window (you can type in it). Key buttons go to other apps correctly once you collapse the panel."),
            new Thickness(18, 0, 18, 12));
        footer.FontSize = 11;
        DockPanel.SetDock(footer, Dock.Bottom);

        _sections[Profiles] = BuildProfiles();
        _sections[Layout] = BuildLayout();
        _sections[General] = BuildGeneral();
        _sections[Phone] = BuildPhone();
        _sections[Obs] = BuildObs();
        _sections[Guide] = BuildGuide();

        var host = new Panel { Margin = new Thickness(14, 0, 18, 10) };
        foreach (var s in _sections.Values) host.Children.Add(s);
        var scroll = new ScrollViewer
        {
            Content = host,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(4, 0, 4, 8),
        };

        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(_nav);
        root.Children.Add(scroll);
        Child = root;

        _loading = false;
        RefreshFromDeck();
        RefreshUpdate();
        ShowSection(_section);
    }

    public void ShowSection(string key)
    {
        if (!_sections.ContainsKey(key)) key = Profiles;
        _section = key;
        foreach (var (k, s) in _sections) s.IsVisible = k == key;
        var item = _nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == key);
        if (item != null && !ReferenceEquals(_nav.SelectedItem, item)) _nav.SelectedItem = item;
        if (key == Phone) LoadAddresses(); // the network may have changed since startup
        RefreshRemote();
    }

    private void RefreshFromDeck()
    {
        bool wasLoading = _loading;
        _loading = true;
        var config = _deck.Config;
        _rows.Value = config.Rows;
        _columns.Value = config.Columns;
        _gridTitle.Text = L.T("Mriežka profilu ", "Grid of profile ") + MainWindow.Quote(config.ActiveProfile.Name);
        UpdateHiddenInfo();
        RefreshProfiles();
        RefreshRemote();
        _loading = wasLoading;
    }

    // ───────────────────────── small builders ─────────────────────────

    private static void OnChange(AvaloniaObject o, AvaloniaProperty property, Action action) =>
        o.PropertyChanged += (_, e) =>
        {
            if (e.Property == property) action();
        };

    private Slider SliderRow(Grid grid, int row, string label, double min, double max, double tick, Action changed)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var text = Ui.FieldLabel(label);
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = tick,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var value = new TextBlock { FontWeight = FontWeight.SemiBold, Foreground = Ui.Label, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        OnChange(slider, RangeBase.ValueProperty, () =>
        {
            value.Text = ((int)slider.Value).ToString();
            if (!_loading) changed();
        });
        Grid.SetRow(text, row);
        Grid.SetRow(slider, row);
        Grid.SetColumn(slider, 1);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 2);
        grid.Children.Add(text);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return slider;
    }

    private static Grid FormGrid(string columns = "110,*,40") => new() { ColumnDefinitions = new ColumnDefinitions(columns) };

    private CheckBox Check(string text, Action changed)
    {
        var box = new CheckBox { Content = text };
        box.Click += (_, _) =>
        {
            if (!_loading) changed();
        };
        return box;
    }

    // ───────────────────────── profiles ─────────────────────────

    private sealed record ProfileItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    private Control BuildProfiles()
    {
        _profileList = new ListBox { MaxHeight = 220, Margin = new Thickness(0, 0, 0, 12) };
        _profileList.SelectionChanged += (_, _) =>
        {
            if (_loading || _profileList.SelectedItem is not ProfileItem item) return;
            _deck.SwitchProfile(item.Id);
        };

        _profileName = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        OnChange(_profileName, TextBox.TextProperty, () =>
        {
            if (_loading || string.IsNullOrWhiteSpace(_profileName.Text)) return;
            var name = _profileName.Text.Trim();
            if (name != _deck.Config.ActiveProfile.Name) _deck.Update(c => c.ActiveProfile.Name = name);
        });

        var sound = Ui.Button(L.T("Profil so zvukom", "Sound profile"), () =>
        {
            _deck.Update(c => c.ActiveProfileId = c.AddMediaProfile().Id);
            RefreshFromDeck();
        });
        ToolTip.SetTip(sound, L.T("Nový profil s tlačidlami na hlasitosť a prehrávanie", "New profile with volume and playback buttons"));

        return Ui.Column(0,
            Ui.Title(L.T("Profily", "Profiles")),
            Ui.Hint(L.T("Klikni na profil a deck sa naň prepne. Každý profil má vlastné tlačidlá a mriežku.",
                "Click a profile to switch to it. Each profile has its own buttons and grid."), new Thickness(0, 0, 0, 8)),
            _profileList,
            Ui.Text(L.T("Názov aktívneho profilu", "Name of the active profile"), opacity: 0.85, margin: new Thickness(0, 0, 0, 6)),
            _profileName,
            Ui.Wrap(
                Ui.Button(L.T("Nový profil", "New profile"), () =>
                {
                    _deck.Update(c => c.ActiveProfileId = c.AddProfile(c.UniqueProfileName(L.T("Nový profil", "New profile"))).Id);
                    RefreshFromDeck();
                    _profileName.Focus();
                    _profileName.SelectAll();
                }),
                Ui.Button(L.T("Duplikovať", "Duplicate"), () =>
                {
                    _deck.Update(c =>
                    {
                        var source = c.ActiveProfile;
                        c.ActiveProfileId = c.DuplicateProfile(source.Id, c.UniqueProfileName(source.Name + L.T(" (kópia)", " (copy)"))).Id;
                    });
                    RefreshFromDeck();
                }),
                Ui.Button(L.T("Vymazať", "Delete"), DeleteProfile),
                sound),
            Ui.Hint(L.T("Tip: tlačidlo s akciou „Prepnúť profil“ funguje ako priečinok. V cieľovom profile si daj tlačidlo späť.",
                "Tip: a button with the action \"Switch profile\" works like a folder. Put a button back in the target profile.")));
    }

    private async void DeleteProfile()
    {
        var profile = _deck.Config.ActiveProfile;
        if (_deck.Config.Profiles.Count <= 1)
        {
            await Dialogs.InfoAsync(Owner, AppInfo.ProductName, L.T("Posledný profil sa vymazať nedá.", "The last profile cannot be deleted."));
            return;
        }
        bool yes = await Dialogs.ConfirmAsync(Owner, L.T("Vymazať profil", "Delete profile"),
            L.T($"Vymazať profil „{profile.Name}“ aj s jeho {profile.Buttons.Count} tlačidlami?",
                $"Delete the profile \"{profile.Name}\" with its {profile.Buttons.Count} buttons?"));
        if (!yes) return;
        var id = profile.Id;
        _deck.Update(c => c.RemoveProfile(id));
        RefreshFromDeck();
    }

    internal static string Plural(int n, string one, string few, string many) =>
        n == 1 ? one : n is >= 2 and <= 4 ? few : many;

    private void RefreshProfiles()
    {
        var config = _deck.Config;
        var activeId = config.ActiveProfile.Id;
        var items = config.Profiles.Select(p => new ProfileItem(p.Id,
            (p.Id == activeId ? "✓  " : "     ") + p.Name + $"   ·   {p.Buttons.Count} " +
            L.T(Plural(p.Buttons.Count, "tlačidlo", "tlačidlá", "tlačidiel"), p.Buttons.Count == 1 ? "button" : "buttons"))).ToList();

        bool wasLoading = _loading;
        _loading = true;
        _profileList.ItemsSource = items;
        _profileList.SelectedItem = items.FirstOrDefault(i => i.Id == activeId);
        // Don't fight the user while they type (the saved name is trimmed, the text box isn't).
        if (!(_profileName.IsFocused && _profileName.Text?.Trim() == config.ActiveProfile.Name))
            _profileName.Text = config.ActiveProfile.Name;
        _loading = wasLoading;
    }

    // ───────────────────────── layout ─────────────────────────

    private Control BuildLayout()
    {
        _gridTitle = Ui.Title("");
        var grid = FormGrid();
        _rows = SliderRow(grid, 0, L.T("Riadky", "Rows"), DeckProfile.MinRows, DeckProfile.MaxRows, 1, ApplyGrid);
        _columns = SliderRow(grid, 1, L.T("Stĺpce", "Columns"), DeckProfile.MinColumns, DeckProfile.MaxColumns, 1, ApplyGrid);
        _hiddenInfo = Ui.Hint("", new Thickness(0, 4, 0, 20));

        var look = FormGrid();
        _size = SliderRow(look, 0, L.T("Veľkosť", "Size"), DeckSettings.MinButtonSize, DeckSettings.MaxButtonSize, 4, ApplyLook);
        _spacing = SliderRow(look, 1, L.T("Medzery", "Spacing"), DeckSettings.MinSpacing, DeckSettings.MaxSpacing, 1, ApplyLook);
        _labels = Check(L.T("Popisky pod ikonami", "Labels under icons"), ApplyLook);

        var s = _deck.Config.Settings;
        _loading = true;
        _size.Value = s.ButtonSize;
        _spacing.Value = s.Spacing;
        _labels.IsChecked = s.ShowLabels;

        return Ui.Column(4, _gridTitle, grid, _hiddenInfo, Ui.Title(L.T("Vzhľad (všetky profily)", "Look (all profiles)")), look, _labels);
    }

    private void ApplyGrid() => _deck.Update(c =>
    {
        c.Rows = (int)_rows.Value;
        c.Columns = (int)_columns.Value;
    });

    private void ApplyLook() => _deck.Update(c =>
    {
        c.Settings.ButtonSize = (int)_size.Value;
        c.Settings.Spacing = (int)_spacing.Value;
        c.Settings.ShowLabels = _labels.IsChecked == true;
    });

    private void UpdateHiddenInfo()
    {
        var config = _deck.Config;
        int hidden = config.Buttons.Count(b => !config.IsVisible(b));
        _hiddenInfo.Text = hidden == 0
            ? L.T("Tlačidlá presunieš ťahaním myšou. Pravý klik na tlačidlo ho upraví.",
                "Drag buttons with the mouse to move them. Right-click a button to edit it.")
            : L.T($"Mimo mriežky je {hidden} tlačidiel. Nevymazali sa – po zväčšení mriežky sa vrátia.",
                $"{hidden} buttons are outside the grid. They were not deleted and come back when the grid grows.");
    }

    // ───────────────────────── general ─────────────────────────

    private Control BuildGeneral()
    {
        var s = _deck.Config.Settings;

        _language = new ComboBox { Width = 170, Items = { "Slovenčina", "English" } };
        _language.SelectedIndex = L.Current == AppLanguage.En ? 1 : 0;
        _language.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var language = _language.SelectedIndex == 1 ? AppLanguage.En : AppLanguage.Sk;
            if (language == L.Current) return;
            Dispatcher.UIThread.Post(() =>
            {
                L.Set(language); // every window re-reads its texts
                _deck.Update(c =>
                {
                    c.Settings.Language = L.Code(language);
                    c.TranslateDefaults(language); // "Hlavný" → "Main", starter button labels…
                }); // phones get the new language too
            });
        };
        var languageRow = FormGrid("120,*");
        var languageLabel = Ui.FieldLabel("Jazyk / Language");
        Grid.SetColumn(_language, 1);
        languageRow.Children.Add(languageLabel);
        languageRow.Children.Add(_language);
        languageRow.Margin = new Thickness(0, 0, 0, 14);

        _topmost = Check(L.T("Vždy navrchu", "Always on top"), ApplyGeneral);
        _autostart = Check(AppInfo.IsMac ? L.T("Spustiť po prihlásení", "Open at login") : L.T("Spustiť so systémom Windows", "Start with Windows"), () =>
        {
            AutoStart.Set(_autostart.IsChecked == true);
            ApplyGeneral();
        });
        _autoUpdate = Check(L.T("Kontrolovať nové verzie automaticky", "Check for new versions automatically"), ApplyGeneral);
        _topmost.IsChecked = s.AlwaysOnTop;
        _autostart.IsChecked = s.StartWithWindows;
        _autoUpdate.IsChecked = s.CheckForUpdates;

        _updateButton = Ui.Button("", async () =>
        {
            if (_updates.Available != null) await _updates.ConfirmAndInstallAsync(Owner);
            else await _updates.CheckAsync();
        });
        _updateStatus = Ui.Text("", 12, margin: new Thickness(0, 8, 0, 4));

        return Ui.Column(6,
            Ui.Title(L.T("Všeobecné", "General")),
            languageRow,
            _topmost,
            _autostart,
            Ui.Text(L.T("Aktualizácie", "Updates"), weight: FontWeight.SemiBold, margin: new Thickness(0, 14, 0, 0)),
            _autoUpdate,
            _updateButton,
            _updateStatus,
            Ui.Hint(AppInfo.IsMac
                ? L.T("Novú verziu stiahne priamo Stream Deck, takže Mac pri spustení nevyskočí s varovaním.",
                    "Stream Deck downloads the new version itself, so macOS does not warn when it starts.")
                : L.T("Novú verziu stiahne priamo Stream Deck, takže Windows pri spustení nevyskočí s varovaním.",
                    "Stream Deck downloads the new version itself, so Windows does not warn when it starts."), new Thickness(0, 0, 0, 14)),
            Ui.Text(L.T("Konfigurácia", "Configuration"), weight: FontWeight.SemiBold),
            Ui.Hint(L.T("Tlačidlá, profily, ikony a nastavenia sú v jednom priečinku. Na zálohu ho stačí skopírovať.",
                "Buttons, profiles, icons and settings are in one folder. Copy it to make a backup."), new Thickness(0, 0, 0, 4)),
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { Ui.Button(L.T("Otvoriť priečinok s nastaveniami", "Open the settings folder"), () => ShellHelper.OpenFolder(_deck.Store.Directory)) },
            },
            Ui.Hint($"{AppInfo.ProductName} {AppInfo.Version}\n{_deck.Store.Directory}", new Thickness(0, 18, 0, 0)));
    }

    private void ApplyGeneral() => _deck.Update(c =>
    {
        c.Settings.AlwaysOnTop = _topmost.IsChecked == true;
        c.Settings.StartWithWindows = _autostart.IsChecked == true;
        c.Settings.CheckForUpdates = _autoUpdate.IsChecked == true;
    });

    private void RefreshUpdate()
    {
        var u = _updates;
        var info = u.Available;
        bool busy = u.Current is UpdateService.State.Checking or UpdateService.State.Downloading or UpdateService.State.Restarting;
        _updateButton.IsEnabled = !busy;
        _updateButton.Content = info != null ? L.T($"Aktualizovať na {info.Version}", $"Update to {info.Version}") : L.T("Skontrolovať teraz", "Check now");

        _updateStatus.Foreground = Ui.Label;
        switch (u.Current)
        {
            case UpdateService.State.Checking:
                _updateStatus.Text = L.T("Kontrolujem…", "Checking…");
                break;
            case UpdateService.State.Downloading:
                _updateStatus.Text = L.T($"Sťahujem verziu {info?.Version}… {u.Percent} %", $"Downloading version {info?.Version}… {u.Percent} %");
                break;
            case UpdateService.State.Restarting:
                _updateStatus.Text = L.T("Spúšťam novú verziu…", "Starting the new version…");
                break;
            case UpdateService.State.Error:
                _updateStatus.Foreground = Ui.Error;
                _updateStatus.Text = u.Error;
                break;
            case UpdateService.State.UpToDate:
                _updateStatus.Foreground = Ui.Ok;
                _updateStatus.Text = L.T($"Máš najnovšiu verziu ({AppInfo.Version}).", $"You have the latest version ({AppInfo.Version}).");
                break;
            case UpdateService.State.Available when info != null:
                _updateStatus.Foreground = Ui.Ok;
                _updateStatus.Text = L.T($"Je dostupná nová verzia {info.Version} (teraz máš {AppInfo.Version}).",
                    $"Version {info.Version} is available (you have {AppInfo.Version}).");
                break;
            default:
                _updateStatus.Text = UpdateService.IsDevBuild
                    ? L.T("Vývojová verzia: automaticky sa nekontroluje.", "Development build: no automatic checks.")
                    : L.T($"Teraz máš verziu {AppInfo.Version}.", $"You have version {AppInfo.Version}.");
                break;
        }
    }

    // ───────────────────────── phone ─────────────────────────

    private Control BuildPhone()
    {
        _remoteEnabled = Check(L.T("Povoliť ovládanie z mobilu", "Allow control from a phone"),
            () => ApplyRemote(c => c.Settings.Remote.Enabled = _remoteEnabled.IsChecked == true));
        _remoteEnabled.IsChecked = _deck.Config.Settings.Remote.Enabled;

        _qr = new Image { Margin = new Thickness(6), Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(_qr, BitmapInterpolationMode.None);

        _address = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _address.SelectionChanged += (_, _) =>
        {
            if (_loading || _address.SelectedItem is not LanAddress a) return;
            _deck.Update(c => c.Settings.Remote.Address = a.Address, notify: false);
            RefreshRemote();
        };
        _remotePort = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Text = _deck.Config.Settings.Remote.Port.ToString() };
        _remotePort.LostFocus += (_, _) => ApplyRemotePort();
        _remotePort.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) ApplyRemotePort();
        };

        var form = FormGrid("90,*");
        form.RowDefinitions = new RowDefinitions("Auto,Auto");
        var addressLabel = Ui.FieldLabel(L.T("Adresa PC", "PC address"));
        var portLabel = Ui.FieldLabel("Port");
        Grid.SetColumn(_address, 1);
        Grid.SetRow(portLabel, 1);
        Grid.SetRow(_remotePort, 1);
        Grid.SetColumn(_remotePort, 1);
        _remotePort.Margin = new Thickness(0, 8, 0, 0);
        portLabel.Margin = new Thickness(0, 8, 12, 0);
        form.Children.AddRange(new Control[] { addressLabel, _address, portLabel, _remotePort });
        form.Margin = new Thickness(0, 0, 0, 10);

        var newCode = Ui.Button(L.T("Nový kód", "New code"), NewToken);
        ToolTip.SetTip(newCode, L.T("Vytvorí nový QR kód. Zariadenia so starým kódom sa odpoja.", "Creates a new QR code. Devices with the old code are disconnected."));
        _url = new TextBox { IsReadOnly = true, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };

        _remotePanel = Ui.Column(0,
            new Border
            {
                Width = 190,
                Height = 190,
                Background = Brushes.White,
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = _qr,
            },
            Ui.Text(L.T("Naskenuj kód fotoaparátom mobilu.", "Scan the code with the phone camera."), weight: FontWeight.SemiBold, margin: new Thickness(0, 10, 0, 12)),
            form,
            Ui.Wrap(Ui.Button(L.T("Kopírovať odkaz", "Copy link"), CopyUrl), newCode),
            _url);

        _remoteStatus = Ui.Text("", 12, margin: new Thickness(0, 10, 0, 0));
        LoadAddresses();

        return Ui.Column(0,
            Ui.Title(L.T("Ovládanie z mobilu", "Phone control")),
            _remoteEnabled,
            Ui.Hint(L.T("Mobil musí byť v rovnakej Wi-Fi. Nič sa neinštaluje, beží to v prehliadači.",
                "The phone must be on the same Wi-Fi. Nothing to install, it runs in the browser."), new Thickness(0, 4, 0, 12)),
            _remotePanel,
            _remoteStatus,
            Ui.Hint(AppInfo.IsMac
                ? L.T("Ak sa mobil nevie pripojiť, povoľ Stream Deck v Nastavenia systému → Sieť → Firewall (prichádzajúce pripojenia).",
                    "If the phone cannot connect, allow Stream Deck in System Settings → Network → Firewall (incoming connections).")
                : L.T("Ak sa mobil nevie pripojiť, povoľ Stream Deck vo Windows Firewall pre súkromné siete. Viac v Návode.",
                    "If the phone cannot connect, allow Stream Deck in Windows Firewall for private networks. More in the Guide."), new Thickness(0, 8, 0, 0)));
    }

    private void LoadAddresses()
    {
        bool wasLoading = _loading;
        _loading = true;
        var addresses = NetworkInfo.GetLanAddresses();
        _address.ItemsSource = addresses;
        var saved = _deck.Config.Settings.Remote.Address;
        _address.SelectedItem = addresses.FirstOrDefault(a => a.Address == saved) ?? addresses.FirstOrDefault();
        _loading = wasLoading;
    }

    private void ApplyRemote(Action<DeckConfig> change)
    {
        _deck.Update(change); // App reacts to Changed and starts / stops / restarts the server
        RefreshRemote();
    }

    private void ApplyRemotePort()
    {
        if (!int.TryParse(_remotePort.Text?.Trim(), out int port) || port is < 1024 or > 65535)
        {
            _remotePort.Text = _deck.Config.Settings.Remote.Port.ToString();
            return;
        }
        if (port != _deck.Config.Settings.Remote.Port) ApplyRemote(c => c.Settings.Remote.Port = port);
    }

    private string? CurrentUrl()
    {
        var r = _deck.Config.Settings.Remote;
        if (_address.SelectedItem is not LanAddress a || string.IsNullOrEmpty(r.Token)) return null;
        return RemoteServer.BuildUrl(a.Address, r.Port, r.Token);
    }

    private void RefreshRemote()
    {
        if (_remoteStatus == null) return;
        var r = _deck.Config.Settings.Remote;
        _remotePanel.IsVisible = r.Enabled;
        bool wasLoading = _loading;
        _loading = true;
        _remoteEnabled.IsChecked = r.Enabled;
        _loading = wasLoading;

        var url = r.Enabled ? CurrentUrl() : null;
        _qr.Source = url != null ? RenderQr(url) : null;
        _url.Text = url ?? "";

        _remoteStatus.Foreground = Ui.Label;
        if (!r.Enabled)
        {
            _remoteStatus.Text = L.T("Vypnuté.", "Off.");
        }
        else if (_remote.LastError is { } error)
        {
            _remoteStatus.Foreground = Ui.Error;
            _remoteStatus.Text = error;
        }
        else if (_address.ItemCount == 0)
        {
            _remoteStatus.Foreground = Ui.Error;
            _remoteStatus.Text = L.T("PC nie je pripojené k žiadnej sieti.", "The PC is not connected to any network.");
        }
        else if (_remote.IsRunning)
        {
            int n = _remote.ClientCount;
            _remoteStatus.Foreground = Ui.Ok;
            _remoteStatus.Text = n == 0
                ? L.T($"Beží na porte {_remote.Port}. Zatiaľ nie je pripojené žiadne zariadenie.", $"Running on port {_remote.Port}. No device connected yet.")
                : L.T($"Beží na porte {_remote.Port}. Pripojené: {n} {Plural(n, "zariadenie", "zariadenia", "zariadení")}.",
                    $"Running on port {_remote.Port}. Connected: {n} {(n == 1 ? "device" : "devices")}.");
        }
        else
        {
            _remoteStatus.Text = L.T("Spúšťam…", "Starting…");
        }
    }

    /// <summary>Black-on-white QR bitmap, 1 pixel per module plus a 4-module quiet zone (scaled up without smoothing).</summary>
    internal static unsafe WriteableBitmap RenderQr(string text)
    {
        var qr = QrCode.Encode(text);
        const int quiet = 4;
        int size = qr.Size + quiet * 2;
        var bmp = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        for (int y = 0; y < size; y++)
        {
            var row = (uint*)((byte*)fb.Address + y * fb.RowBytes);
            for (int x = 0; x < size; x++)
            {
                int qx = x - quiet, qy = y - quiet;
                bool dark = qx >= 0 && qy >= 0 && qx < qr.Size && qy < qr.Size && qr[qy, qx];
                row[x] = dark ? 0xFF000000u : 0xFFFFFFFFu;
            }
        }
        return bmp;
    }

    private async void CopyUrl()
    {
        if (CurrentUrl() is not { } url) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(url);
        }
        catch
        {
            // clipboard busy
        }
    }

    private async void NewToken()
    {
        bool yes = await Dialogs.ConfirmAsync(Owner, L.T("Nový kód", "New code"),
            L.T("Vytvoriť nový QR kód? Všetky pripojené mobily sa odpoja a budú musieť naskenovať nový kód.",
                "Create a new QR code? All connected phones are disconnected and have to scan the new code."));
        if (yes) ApplyRemote(c => c.Settings.Remote.Token = RemoteSettings.NewToken());
    }

    // ───────────────────────── OBS ─────────────────────────

    private Control BuildObs()
    {
        var s = _deck.Config.Settings.Obs;
        _obsHost = new TextBox { Text = s.Host };
        _obsPort = new TextBox { Text = s.Port.ToString() };
        _obsPassword = new TextBox { Text = s.Password, PasswordChar = '•', RevealPassword = false };
        foreach (var box in new[] { _obsHost, _obsPort, _obsPassword }) OnChange(box, TextBox.TextProperty, ApplyObs);
        _obsStatus = Ui.Text("", 12, margin: new Thickness(0, 10, 0, 0));

        var form = FormGrid("80,*,Auto,70");
        form.RowDefinitions = new RowDefinitions("Auto,Auto");
        var hostLabel = Ui.FieldLabel(L.T("Adresa", "Address"));
        var colon = new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), Foreground = Ui.Label };
        var passLabel = Ui.FieldLabel(L.T("Heslo", "Password"));
        Grid.SetColumn(_obsHost, 1);
        Grid.SetColumn(colon, 2);
        Grid.SetColumn(_obsPort, 3);
        Grid.SetRow(passLabel, 1);
        Grid.SetRow(_obsPassword, 1);
        Grid.SetColumn(_obsPassword, 1);
        Grid.SetColumnSpan(_obsPassword, 3);
        passLabel.Margin = new Thickness(0, 10, 12, 0);
        _obsPassword.Margin = new Thickness(0, 10, 0, 0);
        form.Children.AddRange(new Control[] { hostLabel, _obsHost, colon, _obsPort, passLabel, _obsPassword });
        form.Margin = new Thickness(0, 0, 0, 14);

        return Ui.Column(0,
            Ui.Title("OBS Studio"),
            Ui.Hint(L.T("V OBS zapni Nástroje → Nastavenia WebSocket servera → Povoliť WebSocket server. Postup krok za krokom je v Návode.",
                "In OBS turn on Tools → WebSocket Server Settings → Enable WebSocket server. Step by step in the Guide."), new Thickness(0, 0, 0, 14)),
            form,
            new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Button(L.T("Otestovať pripojenie", "Test connection"), TestObs) } },
            _obsStatus);
    }

    private void ApplyObs()
    {
        if (_loading) return;
        _obsStatus.Text = "";
        _deck.Update(c =>
        {
            c.Settings.Obs.Host = _obsHost.Text ?? "";
            c.Settings.Obs.Port = int.TryParse(_obsPort.Text?.Trim(), out var port) ? port : 4455;
            c.Settings.Obs.Password = _obsPassword.Text ?? "";
        }, notify: false);
        _obs.Configure(_deck.Config.Settings.Obs);
    }

    private async void TestObs()
    {
        _obsStatus.Foreground = Ui.Label;
        _obsStatus.Text = L.T("Pripájam…", "Connecting…");
        try
        {
            _obs.Configure(_deck.Config.Settings.Obs);
            var version = await _obs.TestAsync();
            _obsStatus.Foreground = Ui.Ok;
            _obsStatus.Text = L.T($"Pripojené, OBS {version}", $"Connected, OBS {version}");
        }
        catch (Exception ex)
        {
            _obsStatus.Foreground = Ui.Error;
            _obsStatus.Text = ex.Message;
        }
    }

    // ───────────────────────── guide ─────────────────────────

    private static Control BuildGuide()
    {
        var panel = Ui.Column(0, Ui.Title(L.T("Návod", "Guide")));
        foreach (var (title, text) in GuideTexts())
        {
            panel.Children.Add(Ui.Text(title, 14, FontWeight.SemiBold, margin: new Thickness(0, 14, 0, 4)));
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Label, Opacity = 0.85, LineHeight = 20 });
        }
        return panel;
    }

    private static IEnumerable<(string Title, string Text)> GuideTexts()
    {
        bool mac = AppInfo.IsMac;
        string mod = mac ? "⌃⌥1" : "Ctrl+Alt+1";

        yield return (L.T("Ako to funguje", "How it works"), L.T(
            "Deck je mriežka tlačidiel. Každé tlačidlo robí jednu vec: otvorí program alebo web, stlačí klávesovú skratku, ovláda hudbu, OBS alebo prepne profil. Skratka z tlačidla ide do appky, v ktorej práve pracuješ.",
            "The deck is a grid of buttons. Each button does one thing: opens a program or website, presses a key combination, controls music or OBS, or switches the profile. Keys from a button go to the app you are working in."));

        yield return (L.T("Tlačidlá", "Buttons"), L.T(
            "• Pridať: pretiahni program, odkaz, priečinok alebo adresu z prehliadača na mriežku. Alebo klikni na prázdne políčko.\n• Upraviť / vymazať: pravý klik na tlačidlo.\n• Presunúť: chyť tlačidlo myšou a pusti na iné políčko.\n• Ikona: v editore pretiahni obrázok alebo program na náhľad ikony.",
            "• Add: drop a program, shortcut, folder or a link from the browser on the grid. Or click an empty cell.\n• Edit / delete: right-click the button.\n• Move: drag the button onto another cell.\n• Icon: in the editor, drop a picture or program on the icon preview."));

        yield return (L.T("Klávesové skratky", "Hotkeys"), L.T(
            $"V editore tlačidla klikni do poľa Klávesová skratka a stlač kombináciu (napr. {mod}). Tlačidlo sa potom spustí touto skratkou kdekoľvek, aj keď je deck schovaný. Bez modifikátorov sa hodia klávesy F13–F20 alebo numerická klávesnica.",
            $"In the button editor click the Hotkey field and press a combination (e.g. {mod}). The button then runs from anywhere with this hotkey, even when the deck is hidden. Without modifiers, use F13–F20 or the numeric keypad."));

        yield return (L.T("Profily", "Profiles"), L.T(
            "Profil je samostatná sada tlačidiel, napr. „Hry“, „Stream“, „Práca“. Vytvoríš ho v sekcii Profily, prepneš kliknutím na jeho názov, z mobilu alebo tlačidlom s akciou Prepnúť profil. Profil so zvukom pridá hotový profil s tlačidlami na hlasitosť a prehrávanie.",
            "A profile is a separate set of buttons, e.g. \"Games\", \"Stream\", \"Work\". Create one in Profiles, switch by clicking its name, from the phone, or with a button using the action Switch profile. Sound profile adds a ready-made profile with volume and playback buttons."));

        if (mac)
        {
            yield return (L.T("Povolenia na Macu", "Permissions on a Mac"), L.T(
                "Aby tlačidlá mohli posielať klávesy a ovládať hlasitosť, Mac sa raz opýta na povolenie Prístupnosť. Povoľ Stream Deck v Nastavenia systému → Súkromie a bezpečnosť → Prístupnosť. Ak po aktualizácii skratky prestanú fungovať, Stream Deck v tomto zozname vypni a znova zapni.",
                "To send keys and control the volume, macOS asks once for the Accessibility permission. Allow Stream Deck in System Settings → Privacy & Security → Accessibility. If key buttons stop working after an update, turn Stream Deck off and on again in that list."));
        }

        yield return (L.T("Pripojenie OBS", "Connecting OBS"), L.T(
            "1. V OBS otvor Nástroje → Nastavenia WebSocket servera.\n2. Zaškrtni Povoliť WebSocket server a potvrď.\n3. Klikni na Zobraziť informácie o pripojení a skopíruj heslo.\n4. Tu v sekcii OBS vlož heslo (adresa localhost, port 4455) a klikni na Otestovať pripojenie.\n5. Pri tlačidle vyber akciu OBS Studio. Scény a zdroje sa načítajú priamo z OBS.\nAk OBS beží na inom počítači, zadaj jeho IP adresu namiesto localhost.",
            "1. In OBS open Tools → WebSocket Server Settings.\n2. Check Enable WebSocket server and confirm.\n3. Click Show Connect Info and copy the password.\n4. Here in OBS paste the password (address localhost, port 4455) and click Test connection.\n5. For a button choose the action OBS Studio. Scenes and sources are loaded from OBS.\nIf OBS runs on another computer, enter its IP address instead of localhost."));

        yield return (L.T("Ovládanie z mobilu", "Phone control"), L.T(
            "1. Mobil aj počítač musia byť v rovnakej Wi-Fi.\n2. V sekcii Mobil zaškrtni Povoliť ovládanie z mobilu.\n3. " +
            (mac ? "Ak sa Mac opýta na prichádzajúce pripojenia, povoľ ich." : "Ak sa Windows opýta na prístup do siete, povoľ súkromné siete.") +
            "\n4. Naskenuj QR kód fotoaparátom mobilu a otvor odkaz.\n5. V prehliadači daj Pridať na plochu, odvtedy sa deck otvorí ako appka na celú obrazovku.\nTlačidlo Nový kód odpojí všetky mobily.",
            "1. The phone and the computer must be on the same Wi-Fi.\n2. In Phone check Allow control from a phone.\n3. " +
            (mac ? "If macOS asks about incoming connections, allow them." : "If Windows asks about network access, allow private networks.") +
            "\n4. Scan the QR code with the phone camera and open the link.\n5. In the browser choose Add to Home Screen, then the deck opens full screen like an app.\nNew code disconnects all phones."));

        yield return (mac ? L.T("Okno a lišta s menu", "Window and menu bar") : L.T("Okno a lišta", "Window and tray"), L.T(
            (mac ? "Klik na ikonu v lište s menu hore" : "Klik na ikonu v lište vedľa hodín") +
            " deck schová alebo ukáže, appka beží ďalej a skratky fungujú. Zatvorenie okna appku úplne ukončí (opýta sa). Keď vyjde nová verzia, v pruhu s ikonami sa objaví zelená šípka. Klikni na ňu a Stream Deck sa sám aktualizuje a znova spustí.",
            (mac ? "Clicking the icon in the menu bar" : "Clicking the tray icon next to the clock") +
            " hides or shows the deck, the app keeps running and hotkeys work. Closing the window quits the app (it asks first). When a new version is out, a green arrow appears in the icon bar. Click it and Stream Deck updates and restarts itself."));
    }
}
