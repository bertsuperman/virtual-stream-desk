using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;
using VirtualStreamDeck.Core.Remote;

namespace VirtualStreamDeck.App;

/// <summary>
/// Settings, profiles and the guide as a collapsible side panel of the main window.
/// Every change is applied and saved immediately.
/// </summary>
public partial class SettingsPanel : UserControl
{
    public const string Profiles = "profiles", Layout = "layout", General = "general",
        Phone = "phone", Obs = "obs", Guide = "guide";

    private static readonly Brush OkBrush = Frozen(0x5C, 0xCB, 0x6E);
    private static readonly Brush ErrorBrush = Frozen(0xF0, 0x6A, 0x6A);

    private Deck _deck = null!;
    private ObsClient _obs = null!;
    private RemoteServer _remote = null!;
    private UpdateService _updates = null!;
    private bool _loading = true;

    public SettingsPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user clicks the collapse arrow.</summary>
    public event Action? CollapseRequested;

    private void ShowGuideLanguage()
    {
        GuideSk.Visibility = L.Current == AppLanguage.Sk ? Visibility.Visible : Visibility.Collapsed;
        GuideEn.Visibility = L.Current == AppLanguage.En ? Visibility.Visible : Visibility.Collapsed;
    }

    public string CurrentSection => (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? Profiles;

    internal void Initialize(Deck deck, ObsClient obs, RemoteServer remote, UpdateService updates)
    {
        _deck = deck;
        _obs = obs;
        _remote = remote;
        _updates = updates;

        _loading = true;
        var s = deck.Config.Settings;
        SizeSlider.Value = s.ButtonSize;
        SpacingSlider.Value = s.Spacing;
        TopmostBox.IsChecked = s.AlwaysOnTop;
        LabelsBox.IsChecked = s.ShowLabels;
        AutostartBox.IsChecked = s.StartWithWindows;
        AutoUpdateBox.IsChecked = s.CheckForUpdates;
        LanguageBox.SelectedIndex = L.Current == AppLanguage.En ? 1 : 0;
        ShowGuideLanguage();
        VersionText.Text = $"{AppInfo.ProductName} {AppInfo.Version}\n{deck.Store.Directory}";
        ObsHostBox.Text = s.Obs.Host;
        ObsPortBox.Text = s.Obs.Port.ToString();
        ObsPasswordBox.Password = s.Obs.Password;
        RemoteEnabledBox.IsChecked = s.Remote.Enabled;
        RemotePortBox.Text = s.Remote.Port.ToString();
        LoadAddresses();
        Nav.SelectedIndex = 0;
        RefreshFromDeck();
        _loading = false;

        RowsSlider.ValueChanged += (_, _) => ApplyGrid();
        ColumnsSlider.ValueChanged += (_, _) => ApplyGrid();
        SizeSlider.ValueChanged += (_, _) => Apply();
        SpacingSlider.ValueChanged += (_, _) => Apply();
        TopmostBox.Click += (_, _) => Apply();
        LabelsBox.Click += (_, _) => Apply();
        AutostartBox.Click += (_, _) =>
        {
            AutoStart.Set(AutostartBox.IsChecked == true);
            Apply();
        };

        AutoUpdateBox.Click += (_, _) => Apply();
        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (_loading || (LanguageBox.SelectedItem as ComboBoxItem)?.Tag is not string code) return;
            var language = L.Parse(code) ?? AppLanguage.Sk;
            L.Set(language); // every window re-reads its texts
            _deck.Update(c =>
            {
                c.Settings.Language = L.Code(language);
                c.TranslateDefaults(language); // "Hlavný" → "Main", starter button labels…
            }); // phones get the new language too
        };
        L.Changed += () =>
        {
            ShowGuideLanguage();
            bool wasLoading = _loading;
            _loading = true;
            LanguageBox.SelectedIndex = L.Current == AppLanguage.En ? 1 : 0;
            _loading = wasLoading;
            RefreshFromDeck();
            RefreshUpdate();
            ObsStatus.Text = "";
        };
        _updates.Changed += RefreshUpdate;
        RefreshUpdate();

        ProfileNameBox.TextChanged += (_, _) => RenameActiveProfile();

        ObsHostBox.TextChanged += (_, _) => ApplyObs();
        ObsPortBox.TextChanged += (_, _) => ApplyObs();
        ObsPasswordBox.PasswordChanged += (_, _) => ApplyObs();

        RemoteEnabledBox.Click += (_, _) => ApplyRemote(c => c.Settings.Remote.Enabled = RemoteEnabledBox.IsChecked == true);
        RemotePortBox.LostFocus += (_, _) => ApplyRemotePort();
        RemotePortBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyRemotePort(); };
        AddressBox.SelectionChanged += (_, _) =>
        {
            if (_loading || AddressBox.SelectedItem is not LanAddress a) return;
            _deck.Update(c => c.Settings.Remote.Address = a.Address, notify: false);
            RefreshRemote();
        };
        _remote.ClientsChanged += () => Dispatcher.InvokeAsync(RefreshRemote);

        // Changes from anywhere (header, phone, buttons) keep the panel in sync.
        _deck.Changed += (_, _) => Dispatcher.InvokeAsync(RefreshFromDeck);
    }

    public void ShowSection(string key)
    {
        var item = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == key);
        if (item != null) Nav.SelectedItem = item;
        if (key == Phone) LoadAddresses(); // the network may have changed since startup
        RefreshRemote();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesSection == null) return; // during InitializeComponent
        var key = CurrentSection;
        ProfilesSection.Visibility = key == Profiles ? Visibility.Visible : Visibility.Collapsed;
        LayoutSection.Visibility = key == Layout ? Visibility.Visible : Visibility.Collapsed;
        GeneralSection.Visibility = key == General ? Visibility.Visible : Visibility.Collapsed;
        PhoneSection.Visibility = key == Phone ? Visibility.Visible : Visibility.Collapsed;
        ObsSection.Visibility = key == Obs ? Visibility.Visible : Visibility.Collapsed;
        GuideSection.Visibility = key == Guide ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke();

    private void RefreshFromDeck()
    {
        bool wasLoading = _loading;
        _loading = true;
        var config = _deck.Config;
        RowsSlider.Value = config.Rows;
        ColumnsSlider.Value = config.Columns;
        GridTitle.Text = L.T("Mriežka profilu ", "Grid of profile ") + MainWindow.Quote(config.ActiveProfile.Name);
        UpdateHiddenInfo();
        RefreshProfiles();
        RefreshRemote();
        _loading = wasLoading;
    }

    private Window? Owner => Window.GetWindow(this);

    // ───────────────────────── layout / general ─────────────────────────

    private void Apply()
    {
        if (_loading) return;
        _deck.Update(c =>
        {
            c.Settings.ButtonSize = (int)SizeSlider.Value;
            c.Settings.Spacing = (int)SpacingSlider.Value;
            c.Settings.AlwaysOnTop = TopmostBox.IsChecked == true;
            c.Settings.ShowLabels = LabelsBox.IsChecked == true;
            c.Settings.StartWithWindows = AutostartBox.IsChecked == true;
            c.Settings.CheckForUpdates = AutoUpdateBox.IsChecked == true;
        });
    }

    private void ApplyGrid()
    {
        if (_loading) return;
        _deck.Update(c =>
        {
            c.Rows = (int)RowsSlider.Value;
            c.Columns = (int)ColumnsSlider.Value;
        });
    }

    private void UpdateHiddenInfo()
    {
        var config = _deck.Config;
        int hidden = config.Buttons.Count(b => !config.IsVisible(b));
        HiddenInfo.Text = hidden == 0
            ? L.T("Tlačidlá presunieš ťahaním myšou. Pravý klik na tlačidlo ho upraví.",
                "Drag buttons with the mouse to move them. Right-click a button to edit it.")
            : L.T($"Mimo mriežky je {hidden} tlačidiel. Nevymazali sa – po zväčšení mriežky sa vrátia.",
                $"{hidden} buttons are outside the grid. They were not deleted and come back when the grid grows.");
    }

    // ───────────────────────── updates ─────────────────────────

    private void RefreshUpdate()
    {
        var u = _updates;
        var info = u.Available;
        bool busy = u.Current is UpdateService.State.Checking or UpdateService.State.Downloading or UpdateService.State.Restarting;
        UpdateButton.IsEnabled = !busy;
        UpdateButton.Content = info != null
            ? L.T($"Aktualizovať na {info.Version}", $"Update to {info.Version}")
            : L.T("Skontrolovať teraz", "Check now");

        UpdateStatus.ClearValue(TextBlock.ForegroundProperty);
        switch (u.Current)
        {
            case UpdateService.State.Checking:
                UpdateStatus.Text = L.T("Kontrolujem…", "Checking…");
                break;
            case UpdateService.State.Downloading:
                UpdateStatus.Text = L.T($"Sťahujem verziu {info?.Version}… {u.Percent} %", $"Downloading version {info?.Version}… {u.Percent} %");
                break;
            case UpdateService.State.Restarting:
                UpdateStatus.Text = L.T("Spúšťam novú verziu…", "Starting the new version…");
                break;
            case UpdateService.State.Error:
                UpdateStatus.Foreground = ErrorBrush;
                UpdateStatus.Text = u.Error;
                break;
            case UpdateService.State.UpToDate:
                UpdateStatus.Foreground = OkBrush;
                UpdateStatus.Text = L.T($"Máš najnovšiu verziu ({AppInfo.Version}).", $"You have the latest version ({AppInfo.Version}).");
                break;
            case UpdateService.State.Available when info != null:
                UpdateStatus.Foreground = OkBrush;
                UpdateStatus.Text = L.T($"Je dostupná nová verzia {info.Version} (teraz máš {AppInfo.Version}).",
                    $"Version {info.Version} is available (you have {AppInfo.Version}).");
                break;
            default:
                UpdateStatus.Text = UpdateService.IsDevBuild
                    ? L.T("Vývojová verzia: automaticky sa nekontroluje.", "Development build: no automatic checks.")
                    : L.T($"Teraz máš verziu {AppInfo.Version}.", $"You have version {AppInfo.Version}.");
                break;
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updates.Available != null) await _updates.ConfirmAndInstallAsync(Owner);
        else await _updates.CheckAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ShellHelper.OpenFolder(_deck.Store.Directory);

    // ───────────────────────── profiles ─────────────────────────

    private sealed record ProfileItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    private void RefreshProfiles()
    {
        var config = _deck.Config;
        var activeId = config.ActiveProfile.Id;
        var items = config.Profiles.Select(p => new ProfileItem(p.Id,
            (p.Id == activeId ? "✓  " : "     ") + p.Name +
            $"   ·   {p.Buttons.Count} " +
            L.T(Plural(p.Buttons.Count, "tlačidlo", "tlačidlá", "tlačidiel"), p.Buttons.Count == 1 ? "button" : "buttons"))).ToList();

        bool wasLoading = _loading;
        _loading = true;
        ProfileList.ItemsSource = items;
        ProfileList.SelectedItem = items.FirstOrDefault(i => i.Id == activeId);
        // Don't fight the user while they type (the saved name is trimmed, the text box isn't).
        if (!(ProfileNameBox.IsKeyboardFocusWithin && ProfileNameBox.Text.Trim() == config.ActiveProfile.Name))
            ProfileNameBox.Text = config.ActiveProfile.Name;
        _loading = wasLoading;
    }

    internal static string Plural(int n, string one, string few, string many) =>
        n == 1 ? one : n is >= 2 and <= 4 ? few : many;

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileList.SelectedItem is not ProfileItem item) return;
        _deck.SwitchProfile(item.Id);
    }

    private void RenameActiveProfile()
    {
        if (_loading || string.IsNullOrWhiteSpace(ProfileNameBox.Text)) return;
        var name = ProfileNameBox.Text.Trim();
        if (name == _deck.Config.ActiveProfile.Name) return;
        _deck.Update(c => c.ActiveProfile.Name = name);
    }

    private void ProfileNew_Click(object sender, RoutedEventArgs e)
    {
        _deck.Update(c => c.ActiveProfileId = c.AddProfile(c.UniqueProfileName(L.T("Nový profil", "New profile"))).Id);
        RefreshFromDeck();
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
    }

    private void ProfileDuplicate_Click(object sender, RoutedEventArgs e)
    {
        _deck.Update(c =>
        {
            var source = c.ActiveProfile;
            c.ActiveProfileId = c.DuplicateProfile(source.Id, c.UniqueProfileName(source.Name + L.T(" (kópia)", " (copy)"))).Id;
        });
        RefreshFromDeck();
    }

    private void ProfileSound_Click(object sender, RoutedEventArgs e)
    {
        _deck.Update(c => c.ActiveProfileId = c.AddMediaProfile().Id);
        RefreshFromDeck();
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        var profile = _deck.Config.ActiveProfile;
        if (_deck.Config.Profiles.Count <= 1)
        {
            MessageBox.Show(Owner!, L.T("Posledný profil sa vymazať nedá.", "The last profile cannot be deleted."), AppInfo.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(Owner!,
            L.T($"Vymazať profil „{profile.Name}“ aj s jeho {profile.Buttons.Count} tlačidlami?",
                $"Delete the profile \"{profile.Name}\" with its {profile.Buttons.Count} buttons?"),
            L.T("Vymazať profil", "Delete profile"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var id = profile.Id;
        _deck.Update(c => c.RemoveProfile(id));
        RefreshFromDeck();
    }

    // ───────────────────────── phone ─────────────────────────

    private void LoadAddresses()
    {
        bool wasLoading = _loading;
        _loading = true;
        var addresses = NetworkInfo.GetLanAddresses();
        AddressBox.ItemsSource = addresses;
        var saved = _deck.Config.Settings.Remote.Address;
        AddressBox.SelectedItem = addresses.FirstOrDefault(a => a.Address == saved) ?? addresses.FirstOrDefault();
        _loading = wasLoading;
    }

    private void ApplyRemote(Action<DeckConfig> change)
    {
        if (_loading) return;
        _deck.Update(change); // App reacts to Changed and starts / stops / restarts the server
        RefreshRemote();
    }

    private void ApplyRemotePort()
    {
        if (!int.TryParse(RemotePortBox.Text.Trim(), out int port) || port is < 1024 or > 65535)
        {
            RemotePortBox.Text = _deck.Config.Settings.Remote.Port.ToString();
            return;
        }
        if (port != _deck.Config.Settings.Remote.Port) ApplyRemote(c => c.Settings.Remote.Port = port);
    }

    private string? CurrentUrl()
    {
        var r = _deck.Config.Settings.Remote;
        if (AddressBox.SelectedItem is not LanAddress a || string.IsNullOrEmpty(r.Token)) return null;
        return RemoteServer.BuildUrl(a.Address, r.Port, r.Token);
    }

    private void RefreshRemote()
    {
        if (_deck == null) return;
        var r = _deck.Config.Settings.Remote;
        RemotePanel.IsEnabled = r.Enabled;
        RemotePanel.Visibility = r.Enabled ? Visibility.Visible : Visibility.Collapsed;

        var url = r.Enabled ? CurrentUrl() : null;
        QrImage.Source = url != null ? RenderQr(url) : null;
        UrlBox.Text = url ?? "";

        RemoteStatus.ClearValue(TextBlock.ForegroundProperty);
        if (!r.Enabled)
        {
            RemoteStatus.Text = L.T("Vypnuté.", "Off.");
        }
        else if (_remote.LastError is { } error)
        {
            RemoteStatus.Foreground = ErrorBrush;
            RemoteStatus.Text = error;
        }
        else if (AddressBox.Items.Count == 0)
        {
            RemoteStatus.Foreground = ErrorBrush;
            RemoteStatus.Text = L.T("PC nie je pripojené k žiadnej sieti.", "The PC is not connected to any network.");
        }
        else if (_remote.IsRunning)
        {
            int n = _remote.ClientCount;
            RemoteStatus.Foreground = OkBrush;
            RemoteStatus.Text = n == 0
                ? L.T($"Beží na porte {_remote.Port}. Zatiaľ nie je pripojené žiadne zariadenie.",
                    $"Running on port {_remote.Port}. No device connected yet.")
                : L.T($"Beží na porte {_remote.Port}. Pripojené: {n} {Plural(n, "zariadenie", "zariadenia", "zariadení")}.",
                    $"Running on port {_remote.Port}. Connected: {n} {(n == 1 ? "device" : "devices")}.");
        }
        else
        {
            RemoteStatus.Text = L.T("Spúšťam…", "Starting…");
        }
    }

    /// <summary>Black-on-white QR bitmap, 1 pixel per module plus a 4-module quiet zone (scaled up without smoothing).</summary>
    private static BitmapSource RenderQr(string text)
    {
        var qr = QrCode.Encode(text);
        const int quiet = 4;
        int size = qr.Size + quiet * 2;
        var pixels = new byte[size * size];
        Array.Fill(pixels, (byte)255);
        for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++)
                if (qr[y, x]) pixels[(y + quiet) * size + x + quiet] = 0;
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bmp.Freeze();
        return bmp;
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentUrl() is { } url)
        {
            try { Clipboard.SetText(url); } catch { /* clipboard busy */ }
        }
    }

    private void NewToken_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Owner!,
            L.T("Vytvoriť nový QR kód? Všetky pripojené mobily sa odpoja a budú musieť naskenovať nový kód.",
                "Create a new QR code? All connected phones are disconnected and have to scan the new code."),
            L.T("Nový kód", "New code"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) ApplyRemote(c => c.Settings.Remote.Token = RemoteSettings.NewToken());
    }

    // ───────────────────────── OBS ─────────────────────────

    private void ApplyObs()
    {
        if (_loading) return;
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
        ObsStatus.ClearValue(TextBlock.ForegroundProperty);
        ObsStatus.Text = L.T("Pripájam…", "Connecting…");
        try
        {
            _obs.Configure(_deck.Config.Settings.Obs);
            var version = await _obs.TestAsync();
            ObsStatus.Foreground = OkBrush;
            ObsStatus.Text = L.T($"Pripojené, OBS {version}", $"Connected, OBS {version}");
        }
        catch (Exception ex)
        {
            ObsStatus.Foreground = ErrorBrush;
            ObsStatus.Text = ex.Message;
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
