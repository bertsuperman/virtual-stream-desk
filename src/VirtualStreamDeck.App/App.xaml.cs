using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;
using VirtualStreamDeck.Core.Windows;

namespace VirtualStreamDeck.App;

public partial class App : Application
{
    private Deck? _deck;
    private ObsClient? _obs;
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
        _deck = new Deck(store, new WindowsActionExecutor(obs));
        _deck.Load();
        obs.Configure(_deck.Config.Settings.Obs);
        _deck.Changed += (_, _) => obs.Configure(_deck.Config.Settings.Obs);

        // Keep the autostart entry pointing at the current exe location.
        if (_deck.Config.Settings.StartWithWindows) AutoStart.Set(true);

        var main = new MainWindow(_deck, obs);
        _main = main;
        main.Show();

        _tray = new TrayIcon(
            text: $"{AppInfo.ProductName} {AppInfo.Version}",
            toggleWindow: main.ToggleVisibility,
            openSettings: main.OpenSettings,
            exit: ExitApp);

        if (store.LastLoadWarning is { } warning)
            MessageBox.Show(warning, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void ExitApp()
    {
        _tray?.Dispose();
        _tray = null;
        _main?.CloseForExit();
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
            "Virtual Stream Deck", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
