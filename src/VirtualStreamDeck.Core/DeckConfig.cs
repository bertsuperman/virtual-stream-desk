using System.Text.Json.Serialization;

namespace VirtualStreamDeck.Core;

public sealed class DeckConfig
{
    /// <summary>2 = added the "obs" action type and settings.obs.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public DeckSettings Settings { get; set; } = new();
    public List<DeckButton> Buttons { get; set; } = new();

    public DeckButton? GetAt(int row, int col) =>
        Buttons.FirstOrDefault(b => b.Row == row && b.Col == col);

    public DeckButton? FindById(string id) =>
        Buttons.FirstOrDefault(b => b.Id == id);

    /// <summary>Buttons outside the current grid are kept but hidden (and their hotkeys are inactive).</summary>
    public bool IsVisible(DeckButton b) =>
        b.Row >= 0 && b.Row < Settings.Rows && b.Col >= 0 && b.Col < Settings.Columns;

    public IEnumerable<DeckButton> VisibleButtons => Buttons.Where(IsVisible);

    /// <summary>Adds the button or replaces the one with the same id. Anything else on that cell is removed.</summary>
    public void Upsert(DeckButton button)
    {
        Buttons.RemoveAll(b => b.Id == button.Id || (b.Row == button.Row && b.Col == button.Col));
        Buttons.Add(button);
    }

    public bool Remove(string id) => Buttons.RemoveAll(b => b.Id == id) > 0;

    /// <summary>Moves a button to a cell. If the cell is taken the two buttons swap places.</summary>
    public void Move(string id, int row, int col)
    {
        var button = FindById(id);
        if (button == null || (button.Row == row && button.Col == col)) return;

        var other = GetAt(row, col);
        if (other != null)
        {
            other.Row = button.Row;
            other.Col = button.Col;
        }
        button.Row = row;
        button.Col = col;
    }

    /// <summary>First empty visible cell in reading order, starting at (row, col).</summary>
    public (int Row, int Col)? FindEmptyCell(int startRow = 0, int startCol = 0)
    {
        int total = Settings.Rows * Settings.Columns;
        int start = Math.Clamp(startRow * Settings.Columns + startCol, 0, Math.Max(0, total - 1));
        for (int i = 0; i < total; i++)
        {
            int index = (start + i) % total;
            int r = index / Settings.Columns, c = index % Settings.Columns;
            if (GetAt(r, c) == null) return (r, c);
        }
        return null;
    }

    /// <summary>Returns another button that already uses the same hotkey, if any.</summary>
    public DeckButton? FindHotkeyConflict(Hotkey hotkey, string? exceptId)
    {
        foreach (var b in Buttons)
        {
            if (b.Id == exceptId) continue;
            if (Hotkey.TryParse(b.Hotkey, out var other) && other == hotkey) return b;
        }
        return null;
    }
}

public sealed class DeckSettings
{
    public const int MinRows = 1, MaxRows = 10;
    public const int MinColumns = 1, MaxColumns = 12;
    public const int MinButtonSize = 40, MaxButtonSize = 200;
    public const int MinSpacing = 0, MaxSpacing = 40;

    public int Rows { get; set; } = 3;
    public int Columns { get; set; } = 5;
    public int ButtonSize { get; set; } = 80;
    public int Spacing { get; set; } = 8;
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public WindowPosition? Window { get; set; }
    public ObsSettings Obs { get; set; } = new();

    public void Normalize()
    {
        Obs ??= new ObsSettings();
        Obs.Normalize();
        Rows = Math.Clamp(Rows, MinRows, MaxRows);
        Columns = Math.Clamp(Columns, MinColumns, MaxColumns);
        ButtonSize = Math.Clamp(ButtonSize, MinButtonSize, MaxButtonSize);
        Spacing = Math.Clamp(Spacing, MinSpacing, MaxSpacing);
    }
}

/// <summary>Connection to the OBS WebSocket server (OBS → Tools → WebSocket Server Settings).</summary>
public sealed class ObsSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4455;

    /// <summary>Stored in plain text in config.json, like OBS stores it in its own config.</summary>
    public string Password { get; set; } = "";

    public void Normalize()
    {
        Host = string.IsNullOrWhiteSpace(Host) ? "localhost" : Host.Trim();
        Port = Port is > 0 and <= 65535 ? Port : 4455;
        Password ??= "";
    }
}

public sealed class WindowPosition
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class DeckButton
{
    public string Id { get; set; } = NewId();
    public int Row { get; set; }
    public int Col { get; set; }
    public string? Label { get; set; }

    /// <summary>Path relative to the config folder, e.g. "icons/a3f9c1.png".</summary>
    public string? Icon { get; set; }

    /// <summary>Global hotkey, e.g. "Ctrl+Alt+1". Null = none.</summary>
    public string? Hotkey { get; set; }

    public DeckAction? Action { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenAction), "open")]
[JsonDerivedType(typeof(KeysAction), "keys")]
[JsonDerivedType(typeof(MediaAction), "media")]
[JsonDerivedType(typeof(ObsAction), "obs")]
public abstract class DeckAction
{
}

/// <summary>Starts a program, opens a file, folder or URL (via ShellExecute).</summary>
public sealed class OpenAction : DeckAction
{
    public string Target { get; set; } = "";
    public string? Args { get; set; }

    [JsonIgnore]
    public bool IsUrl =>
        Uri.TryCreate(Target.Trim(), UriKind.Absolute, out var uri) && !uri.IsFile && !string.IsNullOrEmpty(uri.Scheme);
}

/// <summary>Sends a key combination to the active window.</summary>
public sealed class KeysAction : DeckAction
{
    public string Keys { get; set; } = "";
}

public enum MediaCommand
{
    PlayPause,
    Next,
    Prev,
    Stop,
    VolumeUp,
    VolumeDown,
    Mute,
}

public sealed class MediaAction : DeckAction
{
    public MediaCommand Command { get; set; }

    [JsonIgnore]
    public int VirtualKey => Command switch
    {
        MediaCommand.PlayPause => 0xB3,
        MediaCommand.Next => 0xB0,
        MediaCommand.Prev => 0xB1,
        MediaCommand.Stop => 0xB2,
        MediaCommand.VolumeUp => 0xAF,
        MediaCommand.VolumeDown => 0xAE,
        MediaCommand.Mute => 0xAD,
        _ => throw new ArgumentOutOfRangeException(nameof(Command)),
    };
}

public enum ObsCommand
{
    SwitchScene,
    ToggleRecord,
    ToggleStream,
    ToggleMute,
    ToggleSource,
    SaveReplay,
}

/// <summary>Controls OBS through its built-in WebSocket server.</summary>
public sealed class ObsAction : DeckAction
{
    public ObsCommand Command { get; set; }

    /// <summary>SwitchScene: target scene. ToggleSource: scene containing the source (null = current scene).</summary>
    public string? Scene { get; set; }

    /// <summary>ToggleMute: audio source. ToggleSource: source to show/hide.</summary>
    public string? Source { get; set; }
}
