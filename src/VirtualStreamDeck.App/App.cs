using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.App.UI;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;
using VirtualStreamDeck.Core.Remote;

namespace VirtualStreamDeck.App;

internal sealed class App : Application
{
    /// <summary>Set by "--screenshots": the windows are created by the screenshot code instead.</summary>
    public static bool ScreenshotMode { get; set; }

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Deck? _deck;
    private ObsClient? _obs;
    private RemoteServer? _remote;
    private HotkeyService? _hotkeys;
    private MainWindow? _main;
    private TrayIcon? _tray;
    private NativeMenuItem? _trayShow, _traySettings, _trayQuit;
    private bool _cleanedUp;

    public static App? Instance => Current as App;

    public override void Initialize()
    {
        Name = AppInfo.ProductName;
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (!ScreenshotMode && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => Cleanup(); // ⌘Q on a Mac, Windows logoff
            Dispatcher.UIThread.UnhandledException += OnUnhandledException;
            Dispatcher.UIThread.Post(async () => await StartAsync());
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync()
    {
        var store = new ConfigStore(ConfigStore.ResolveDirectory(AppContext.BaseDirectory));
        var obs = new ObsClient();
        _obs = obs;
        var focus = new FocusService();
        var deck = new Deck(store, new FocusAwareExecutor(ActionExecutors.ForCurrentPlatform(obs), focus));
        _deck = deck;
        deck.Load();
        await SetUpLanguageAsync(deck, store.LoadedNew);

        var remote = new RemoteServer(deck);
        _remote = remote;
        obs.Configure(deck.Config.Settings.Obs);
        ApplyRemote();
        // Changes can come from the phone (profile switch) on a background thread.
        deck.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            obs.Configure(deck.Config.Settings.Obs);
            ApplyRemote();
        });

        // Keep the autostart entry pointing at the current location.
        if (deck.Config.Settings.StartWithWindows) AutoStart.Set(true);

        var updates = new UpdateService(deck);
        _hotkeys = HotkeyService.Create();
        var main = new MainWindow(deck, obs, remote, updates, _hotkeys);
        _main = main;
        main.RestorePosition();
        main.Show();
        focus.Attach(main);
        remote.ButtonPressed += id => Dispatcher.UIThread.Post(() => main.FlashButton(id));

        CreateTrayAndMenu();
        updates.Start();

        if (store.LastLoadWarning is { } warning) await Dialogs.InfoAsync(main, AppInfo.ProductName, warning);
    }

    /// <summary>Asks for the language on first start; a brand new deck starts with volume and playback buttons.</summary>
    private static async Task SetUpLanguageAsync(Deck deck, bool firstStart)
    {
        var language = L.Parse(deck.Config.Settings.Language);
        bool ask = language == null;
        if (ask) language = await LanguageWindow.AskAsync();
        L.Set(language!.Value);

        if (!ask && !firstStart) return;
        deck.Update(c =>
        {
            c.Settings.Language = L.Code(language.Value);
            c.TranslateDefaults(language.Value); // e.g. an existing "Hlavný" profile when English is picked
            if (firstStart)
            {
                c.ActiveProfile.Name = DeckConfig.DefaultProfileName; // in the chosen language
                DeckConfig.AddMediaButtons(c.ActiveProfile);
            }
        }, notify: false);
    }

    /// <summary>Tray icon (Windows) / menu bar icon (macOS) and the app menu.</summary>
    private void CreateTrayAndMenu()
    {
        _trayShow = new NativeMenuItem();
        _trayShow.Click += (_, _) => _main?.ToggleVisibility();
        _traySettings = new NativeMenuItem();
        _traySettings.Click += (_, _) => _main?.OpenSettings();
        _trayQuit = new NativeMenuItem();
        _trayQuit.Click += (_, _) => ExitApp();
        var menu = new NativeMenu();
        menu.Items.Add(_trayShow);
        menu.Items.Add(_traySettings);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_trayQuit);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://VirtualStreamDeck/Assets/app.png"))),
            ToolTipText = $"{AppInfo.ProductName} {AppInfo.Version}",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => _main?.ToggleVisibility();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });

        TrayTexts();
        L.Changed += () => Dispatcher.UIThread.Post(TrayTexts);
    }

    private void TrayTexts()
    {
        if (_trayShow == null || _traySettings == null || _trayQuit == null) return;
        _trayShow.Header = L.T("Zobraziť / skryť", "Show / hide");
        _traySettings.Header = L.T("Nastavenia…", "Settings…");
        _trayQuit.Header = L.T("Ukončiť", "Quit");
    }

    public void ShowDeck() => _main?.ShowDeck();

    /// <summary>Starts, restarts or stops the phone web server to match the settings.</summary>
    private void ApplyRemote()
    {
        if (_deck == null || _remote == null) return;
        var r = _deck.Config.Settings.Remote;
        if (!r.Enabled)
        {
            if (_remote.IsRunning) _remote.Stop();
            return;
        }

        if (string.IsNullOrEmpty(r.Token))
            _deck.Update(c => c.Settings.Remote.Token = RemoteSettings.NewToken(), notify: false);
        var token = _deck.Config.Settings.Remote.Token;

        if (_remote.IsRunning && _remote.Port == r.Port)
        {
            _remote.SetToken(token);
            return;
        }
        try
        {
            _remote.Start(r.Port, token);
        }
        catch (RemoteException)
        {
            // shown in Settings → Phone (RemoteServer.LastError)
        }
    }

    public void ExitApp()
    {
        Cleanup();
        _desktop?.Shutdown();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        if (_tray != null) _tray.IsVisible = false;
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _main?.CloseForExit();
        _remote?.Dispose();
        _obs?.Dispose();
        SingleInstance.Release();
    }

    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = _deck?.Store.Directory ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch
        {
            // nothing else we can do
        }
        e.Handled = true;
        _ = Dialogs.InfoAsync(_main, AppInfo.ProductName,
            L.T("Nastala neočakávaná chyba:\n\n", "An unexpected error occurred:\n\n") + e.Exception.Message);
    }
}
