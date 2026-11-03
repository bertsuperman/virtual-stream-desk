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
    private bool _loading = true;

    public SettingsPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user clicks the collapse arrow.</summary>
    public event Action? CollapseRequested;

    public string CurrentSection => (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? Profiles;

    internal void Initialize(Deck deck, ObsClient obs, RemoteServer remote)
    {
        _deck = deck;
        _obs = obs;
        _remote = remote;

        _loading = true;
        var s = deck.Config.Settings;
        SizeSlider.Value = s.ButtonSize;
        SpacingSlider.Value = s.Spacing;
        TopmostBox.IsChecked = s.AlwaysOnTop;
        LabelsBox.IsChecked = s.ShowLabels;
        AutostartBox.IsChecked = s.StartWithWindows;
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
        GridTitle.Text = $"Mriežka profilu „{config.ActiveProfile.Name}“";
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
            ? "Tlačidlá presunieš ťahaním myšou. Pravý klik na tlačidlo ho upraví."
            : $"Mimo mriežky je {hidden} tlačidiel. Nevymazali sa – po zväčšení mriežky sa vrátia.";
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
            $"   ·   {p.Buttons.Count} {Plural(p.Buttons.Count, "tlačidlo", "tlačidlá", "tlačidiel")}")).ToList();

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
        _deck.Update(c => c.ActiveProfileId = c.AddProfile(c.UniqueProfileName("Nový profil")).Id);
        RefreshFromDeck();
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
    }

    private void ProfileDuplicate_Click(object sender, RoutedEventArgs e)
    {
        _deck.Update(c =>
        {
            var source = c.ActiveProfile;
            c.ActiveProfileId = c.DuplicateProfile(source.Id, c.UniqueProfileName(source.Name + " (kópia)")).Id;
        });
        RefreshFromDeck();
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        var profile = _deck.Config.ActiveProfile;
        if (_deck.Config.Profiles.Count <= 1)
        {
            MessageBox.Show(Owner!, "Posledný profil sa vymazať nedá.", AppInfo.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(Owner!,
            $"Vymazať profil „{profile.Name}“ aj s jeho {profile.Buttons.Count} tlačidlami?",
            "Vymazať profil", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
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
            RemoteStatus.Text = "Vypnuté.";
        }
        else if (_remote.LastError is { } error)
        {
            RemoteStatus.Foreground = ErrorBrush;
            RemoteStatus.Text = error;
        }
        else if (AddressBox.Items.Count == 0)
        {
            RemoteStatus.Foreground = ErrorBrush;
            RemoteStatus.Text = "PC nie je pripojené k žiadnej sieti.";
        }
        else if (_remote.IsRunning)
        {
            int n = _remote.ClientCount;
            RemoteStatus.Foreground = OkBrush;
            RemoteStatus.Text = n == 0
                ? $"Beží na porte {_remote.Port}. Zatiaľ nie je pripojené žiadne zariadenie."
                : $"Beží na porte {_remote.Port}. Pripojené: {n} {Plural(n, "zariadenie", "zariadenia", "zariadení")}.";
        }
        else
        {
            RemoteStatus.Text = "Spúšťam…";
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
            "Vytvoriť nový QR kód? Všetky pripojené mobily sa odpoja a budú musieť naskenovať nový kód.",
            "Nový kód", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
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

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
