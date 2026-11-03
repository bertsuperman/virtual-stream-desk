using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;
using VirtualStreamDeck.Core.Remote;
using VirtualStreamDeck.Core.Windows;

namespace VirtualStreamDeck.App;

public partial class App : Application
{
    private Deck? _deck;
    private ObsClient? _obs;
    private RemoteServer? _remote;
    private MainWindow? _main;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // Same build launched again: just show the running window.
        // A different build (e.g. a newer download): the running one quits and this one takes over.
        if (!SingleInstance.TryAcquire(
                onShowRequest: () => Dispatcher.InvokeAsync(() => _main?.ShowDeck()),
                onExitRequest: () => Dispatcher.InvokeAsync(ExitApp)))
        {
            Shutdown();
            return;
        }

        var store = new ConfigStore(ConfigStore.ResolveDirectory(AppContext.BaseDirectory));
        var obs = new ObsClient();
        _obs = obs;
        var deck = new Deck(store, new WindowsActionExecutor(obs));
        _deck = deck;
        deck.Load();
        var remote = new RemoteServer(deck);
        _remote = remote;

        obs.Configure(deck.Config.Settings.Obs);
        ApplyRemote();
        // Changes can come from the phone (profile switch) on a background thread.
        deck.Changed += (_, _) => OnUiThread(() =>
        {
            obs.Configure(deck.Config.Settings.Obs);
            ApplyRemote();
        });

        // Keep the autostart entry pointing at the current exe location.
        if (deck.Config.Settings.StartWithWindows) AutoStart.Set(true);

        var updates = new UpdateService(deck);

        var main = new MainWindow(deck, obs, remote, updates);
        _main = main;
        main.Show();
        remote.ButtonPressed += id => Dispatcher.InvokeAsync(() => main.FlashButton(id));

        _tray = new TrayIcon(
            text: $"{AppInfo.ProductName} {AppInfo.Version}",
            toggleWindow: main.ToggleVisibility,
            openSettings: () => main.OpenSettings(),
            exit: ExitApp);

        updates.Start();

        if (store.LastLoadWarning is { } warning)
            MessageBox.Show(warning, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.InvokeAsync(action);
    }

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
            // shown in Settings → Mobil (RemoteServer.LastError)
        }
    }

    public void ExitApp()
    {
        _tray?.Dispose();
        _tray = null;
        _main?.CloseForExit();
        _remote?.Dispose();
        _obs?.Dispose();
        SingleInstance.Release();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
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

        MessageBox.Show("Nastala neočakávaná chyba:\n\n" + e.Exception.Message,
            AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
