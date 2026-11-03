namespace VirtualStreamDeck.Core;

public interface IActionExecutor
{
    /// <summary>Runs the action. Throws with a user-friendly message on failure.</summary>
    void Execute(DeckAction action);
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

    public Deck(ConfigStore store, IActionExecutor executor)
    {
        _store = store;
        _executor = executor;
    }

    public DeckConfig Config { get; private set; } = new();
    public ConfigStore Store => _store;

    /// <summary>Raised after a change that affects the layout or hotkeys.</summary>
    public event EventHandler? Changed;

    public void Load()
    {
        Config = _store.Load();
        _store.CleanupIcons(Config);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a change, saves it and (unless <paramref name="notify"/> is false) raises <see cref="Changed"/>.</summary>
    public void Update(Action<DeckConfig> mutate, bool notify = true)
    {
        mutate(Config);
        Config.Settings.Normalize();
        _store.Save(Config);
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<PressResult> PressAsync(string buttonId)
    {
        var button = Config.FindById(buttonId);
        if (button == null || !Config.IsVisible(button))
            return Task.FromResult(PressResult.Fail("Tlačidlo neexistuje."));
        if (button.Action == null)
            return Task.FromResult(PressResult.Ok);

        var action = button.Action;
        // Actions may wait for the user to release modifier keys, so never run them on the UI thread.
        return Task.Run(() =>
        {
            try
            {
                _executor.Execute(action);
                return PressResult.Ok;
            }
            catch (Exception ex)
            {
                return PressResult.Fail(ex.Message);
            }
        });
    }
}
