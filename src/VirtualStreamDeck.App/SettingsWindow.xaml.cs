using System;
using System.Collections.Generic;
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

/// <summary>Every change is applied and saved immediately, so the deck updates live.</summary>
public partial class SettingsWindow : Window
{
    public const string ProfilesTab = "Profily";

    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0xCB, 0x6E));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x6A, 0x6A));

    private readonly Deck _deck;
    private readonly ObsClient _obs;
    private readonly RemoteServer _remote;
    private bool _loading;

    internal SettingsWindow(Deck deck, ObsClient obs, RemoteServer remote, string? tab = null)
    {
        InitializeComponent();
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
        RefreshFromDeck();
        _loading = false;

        // General / layout
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

        // Profiles
        ProfileList.SelectionChanged += (_, _) => ShowSelectedProfileName();
        ProfileNameBox.TextChanged += (_, _) => RenameSelectedProfile();

        // OBS
        ObsHostBox.TextChanged += (_, _) => ApplyObs();
        ObsPortBox.TextChanged += (_, _) => ApplyObs();
        ObsPasswordBox.PasswordChanged += (_, _) => ApplyObs();

        // Phone
        RemoteEnabledBox.Click += (_, _) => ApplyRemote(c => c.Settings.Remote.Enabled = RemoteEnabledBox.IsChecked == true);
        RemotePortBox.LostFocus += (_, _) => ApplyRemotePort();
        RemotePortBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyRemotePort(); };
        AddressBox.SelectionChanged += (_, _) =>
        {
            if (_loading || AddressBox.SelectedItem is not LanAddress a) return;
            _deck.Update(c => c.Settings.Remote.Address = a.Address, notify: false);
            RefreshRemote();
        };
        _remote.ClientsChanged += OnClientsChanged;

        // The phone can switch profiles while this window is open.
        _deck.Changed += OnDeckChanged;
        Closed += (_, _) =>
        {
            _deck.Changed -= OnDeckChanged;
            _remote.ClientsChanged -= OnClientsChanged;
        };

        if (tab != null)
            Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => (string)t.Header == tab) ?? Tabs.SelectedItem;
    }

    private void OnDeckChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(RefreshFromDeck);

    private void OnClientsChanged() => Dispatcher.InvokeAsync(RefreshRemote);

    /// <summary>Re-reads everything that depends on the active profile.</summary>
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

    // ───────────────────────── general / layout ─────────────────────────

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

    private string? SelectedProfileId => (ProfileList.SelectedItem as ProfileItem)?.Id;

    private void RefreshProfiles(string? select = null)
    {
        var config = _deck.Config;
        select ??= SelectedProfileId ?? config.ActiveProfile.Id;
        var items = config.Profiles.Select(p => new ProfileItem(p.Id,
            $"{p.Name}   ·   {p.Buttons.Count} {Plural(p.Buttons.Count, "tlačidlo", "tlačidlá", "tlačidiel")}" +
            (p.Id == config.ActiveProfile.Id ? "   ✓ aktívny" : ""))).ToList();

        bool wasLoading = _loading;
        _loading = true;
        ProfileList.ItemsSource = items;
        ProfileList.SelectedItem = items.FirstOrDefault(i => i.Id == select) ?? items.FirstOrDefault();
        ShowSelectedProfileName();
        _loading = wasLoading;
    }

    private static string Plural(int n, string one, string few, string many) =>
        n == 1 ? one : n is >= 2 and <= 4 ? few : many;

    private void ShowSelectedProfileName()
    {
        var profile = _deck.Config.FindProfile(SelectedProfileId);
        // Don't fight the user while they type (the saved name is trimmed, the text box isn't).
        if (ProfileNameBox.IsKeyboardFocusWithin && profile != null &&
            ProfileNameBox.Text.Trim() == profile.Name) return;
        bool wasLoading = _loading;
        _loading = true;
        ProfileNameBox.Text = profile?.Name ?? "";
        ProfileNameBox.IsEnabled = profile != null;
        _loading = wasLoading;
    }

    private void RenameSelectedProfile()
    {
        if (_loading || SelectedProfileId is not { } id || string.IsNullOrWhiteSpace(ProfileNameBox.Text)) return;
        var name = ProfileNameBox.Text.Trim();
        _deck.Update(c =>
        {
            if (c.FindProfile(id) is { } p) p.Name = name;
        });
    }

    private void ProfileActivate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfileId is { } id) _deck.SwitchProfile(id);
    }

    private void ProfileList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ProfileActivate_Click(sender, e);

    private void ProfileNew_Click(object sender, RoutedEventArgs e)
    {
        string id = "";
        _deck.Update(c =>
        {
            var p = c.AddProfile(c.UniqueProfileName("Nový profil"));
            id = p.Id;
            c.ActiveProfileId = id; // switch right away so you can start adding buttons
        });
        RefreshProfiles(id);
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
    }

    private void ProfileDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfileId is not { } sourceId) return;
        string id = "";
        _deck.Update(c =>
        {
            var source = c.FindProfile(sourceId)!;
            id = c.DuplicateProfile(sourceId, c.UniqueProfileName(source.Name + " (kópia)")).Id;
        });
        RefreshProfiles(id);
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        var profile = _deck.Config.FindProfile(SelectedProfileId);
        if (profile == null) return;
        if (_deck.Config.Profiles.Count <= 1)
        {
            MessageBox.Show(this, "Posledný profil sa vymazať nedá.", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            $"Vymazať profil „{profile.Name}“ aj s jeho {profile.Buttons.Count} tlačidlami?",
            "Vymazať profil", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var id = profile.Id;
        _deck.Update(c => c.RemoveProfile(id));
        RefreshProfiles(_deck.Config.ActiveProfile.Id);
    }

    // ───────────────────────── phone ─────────────────────────

    private void LoadAddresses()
    {
        var addresses = NetworkInfo.GetLanAddresses();
        AddressBox.ItemsSource = addresses;
        var saved = _deck.Config.Settings.Remote.Address;
        AddressBox.SelectedItem = addresses.FirstOrDefault(a => a.Address == saved) ?? addresses.FirstOrDefault();
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
        var r = _deck.Config.Settings.Remote;
        RemotePanel.IsEnabled = r.Enabled;
        RemotePanel.Opacity = r.Enabled ? 1 : 0.35;
        UrlBox.Visibility = r.Enabled ? Visibility.Visible : Visibility.Collapsed;

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
        var answer = MessageBox.Show(this,
            "Vytvoriť nový QR kód? Všetky pripojené mobily sa odpoja a budú musieť naskenovať nový kód.",
            "Nový kód", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) ApplyRemote(c => c.Settings.Remote.Token = RemoteSettings.NewToken());
    }

    // ───────────────────────── OBS ─────────────────────────

    /// <summary>OBS fields change on every keystroke, so save silently (no grid rebuild) and just reconfigure the client.</summary>
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
}
