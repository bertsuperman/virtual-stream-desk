using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Update;

namespace VirtualStreamDeck.App.Services;

/// <summary>
/// Checks GitHub for a newer build and installs it with one click (see <see cref="Updater"/>).
/// All members are used on the UI thread; <see cref="Changed"/> is raised there too.
/// </summary>
internal sealed class UpdateService
{
    public enum State { Idle, Checking, UpToDate, Available, Downloading, Restarting, Error }

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly HttpClient Http = Updater.CreateHttpClient();

    private readonly Deck _deck;
    private readonly DispatcherTimer _timer;
    private readonly string? _exe = Environment.ProcessPath;

    public UpdateService(Deck deck)
    {
        _deck = deck;
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += async (_, _) => await AutoCheckAsync();
    }

    public State Current { get; private set; } = State.Idle;
    public UpdateInfo? Available { get; private set; }
    public int Percent { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Local builds are 0.2.0: they don't look for updates on their own.</summary>
    public static bool IsDevBuild => AppInfo.Version is "0.2.0" or "0.0.0";

    public event Action? Changed;

    public void Start()
    {
        if (_exe != null) Updater.Cleanup(_exe);
        _timer.Start();
        // Give the app (and a just-replaced old instance) a moment before going online.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        delay.Tick += async (_, _) =>
        {
            delay.Stop();
            if (_exe != null) Updater.Cleanup(_exe); // the old instance has exited by now
            await AutoCheckAsync();
        };
        delay.Start();
    }

    private async Task AutoCheckAsync()
    {
        if (IsDevBuild || !_deck.Config.Settings.CheckForUpdates) return;
        if (Current is State.Checking or State.Downloading or State.Restarting) return;
        await CheckAsync(quiet: true);
    }

    /// <summary>Asks GitHub for the newest build. <paramref name="quiet"/>: network errors are not shown.</summary>
    public async Task CheckAsync(bool quiet = false)
    {
        if (Current is State.Checking or State.Downloading or State.Restarting) return;
        var before = Current;
        Set(State.Checking);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var info = await Updater.CheckAsync(Http, AppInfo.Version, ct: cts.Token);
            Available = info;
            Set(info != null ? State.Available : State.UpToDate);
        }
        catch (Exception ex) when (ex is UpdateException or OperationCanceledException)
        {
            if (quiet)
            {
                Set(before); // background check: stay silent, try again later
                return;
            }
            Error = ex is UpdateException ? ex.Message : "GitHub neodpovedá. Skús to neskôr.";
            Set(State.Error);
        }
    }

    /// <summary>Asks, downloads, swaps the exe and starts the new build (which then replaces this instance).</summary>
    public async Task ConfirmAndInstallAsync(Window? owner)
    {
        if (Available is not { } info || Current is State.Downloading or State.Restarting || _exe == null) return;

        var text = $"Aktualizovať na verziu {info.Version}?\n\n" +
                   $"Teraz máš {AppInfo.Version}. Stream Deck sa na pár sekúnd zavrie a sám sa znova spustí. " +
                   "Tlačidlá a nastavenia ostanú.";
        var answer = owner != null
            ? MessageBox.Show(owner, text, "Aktualizácia", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
            : MessageBox.Show(text, "Aktualizácia", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return;

        Percent = 0;
        Set(State.Downloading);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var progress = new Progress<int>(p =>
            {
                Percent = p;
                Changed?.Invoke();
            });
            var downloaded = await Updater.DownloadAsync(Http, info, _exe, progress, ct: cts.Token);
            Updater.Install(downloaded, _exe);
        }
        catch (Exception ex) when (ex is UpdateException or OperationCanceledException)
        {
            Error = ex is UpdateException ? ex.Message : "Sťahovanie trvalo príliš dlho. Skús to znova.";
            Set(State.Error);
            return;
        }

        Set(State.Restarting);
        try
        {
            // The new build sees a different version and asks this instance to quit (SingleInstance).
            Process.Start(new ProcessStartInfo(_exe) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Error = $"Nová verzia je stiahnutá, ale nepodarilo sa ju spustiť ({ex.Message}). Ukonči Stream Deck a spusti ho znova.";
            Set(State.Error);
            return;
        }

        // Normally this instance is gone within a few seconds.
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (Current != State.Restarting) return;
        Error = "Nová verzia je pripravená, ale nespustila sa. Ukonči Stream Deck a spusti ho znova.";
        Set(State.Error);
    }

    private void Set(State state)
    {
        Current = state;
        if (state != State.Error) Error = null;
        Changed?.Invoke();
    }
}
