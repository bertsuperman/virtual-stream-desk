namespace VirtualStreamDeck.Core;

public interface IActionExecutor
{
    /// <summary>Runs the action. Throws with a user-friendly message on failure.</summary>
    Task ExecuteAsync(DeckAction action);
}

public readonly record struct PressResult(bool Success, string? Error)
{
    public static readonly PressResult Ok = new(true, null);
    public static PressResult Fail(string error) => new(false, error);
}

/// <summary>
/// The single entry point for everything that changes or triggers the deck.
/// UI (and later the mobile server) only calls <see cref="Update"/> and <see cref="PressAsync"/>.
/// </summary>
public sealed class Deck
{
    private readonly ConfigStore _store;
    private readonly IActionExecutor _executor;
    private readonly object _gate = new();

    public Deck(ConfigStore store, IActionExecutor executor)
    {
        _store = store;
        _executor = executor;
    }

    public DeckConfig Config { get; private set; } = new();
    public ConfigStore Store => _store;

    /// <summary>Raised after a change that affects the layout or hotkeys (on the thread that called Update).</summary>
    public event EventHandler? Changed;

    /// <summary>Runs <paramref name="read"/> while no update can happen. For readers on other threads (web server).</summary>
    public T Read<T>(Func<DeckConfig, T> read)
    {
        lock (_gate) return read(Config);
    }

    public void Load()
    {
        Config = _store.Load();
        _store.CleanupIcons(Config);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a change, saves it and (unless <paramref name="notify"/> is false) raises <see cref="Changed"/>.</summary>
    public void Update(Action<DeckConfig> mutate, bool notify = true)
    {
        lock (_gate)
        {
            mutate(Config);
            Config.Normalize();
            _store.Save(Config);
        }
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes another profile active. Raises <see cref="Changed"/> on the calling thread.</summary>
    public bool SwitchProfile(string profileId)
    {
        if (Read(c => c.FindProfile(profileId)) == null) return false;
        if (Read(c => c.ActiveProfileId) == profileId) return true;
        Update(c => c.ActiveProfileId = profileId);
        return true;
    }

    public Task<PressResult> PressAsync(string buttonId)
    {
        DeckAction? action;
        lock (_gate)
        {
            var button = Config.FindById(buttonId);
            if (button == null || !Config.IsVisible(button))
                return Task.FromResult(PressResult.Fail(L.T("Tlačidlo neexistuje.", "The button does not exist.")));
            action = button.Action;
        }
        if (action == null)
            return Task.FromResult(PressResult.Ok);

        if (action is ProfileAction profile)
        {
            return Task.FromResult(SwitchProfile(profile.ProfileId)
                ? PressResult.Ok
                : PressResult.Fail(L.T("Tento profil už neexistuje. Uprav tlačidlo.", "This profile no longer exists. Edit the button.")));
        }
        // Actions may wait for the user to release modifier keys, so never run them on the UI thread.
        return Task.Run(async () =>
        {
            try
            {
                await _executor.ExecuteAsync(action);
                return PressResult.Ok;
            }
            catch (Exception ex)
            {
                return PressResult.Fail(ex.Message);
            }
        });
    }
}
