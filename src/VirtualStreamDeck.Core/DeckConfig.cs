using System.Text.Json.Serialization;

namespace VirtualStreamDeck.Core;

public sealed class DeckConfig
{
    /// <summary>
    /// 2 = "obs" action type and settings.obs.
    /// 3 = profiles (rows, columns and buttons moved into profiles[]), "profile" action, settings.remote.
    /// </summary>
    public const int CurrentVersion = 3;
    public static string DefaultProfileName => L.T("Hlavný", "Main");

    public int Version { get; set; } = CurrentVersion;
    public DeckSettings Settings { get; set; } = new();
    public List<DeckProfile> Profiles { get; set; } = new();
    public string? ActiveProfileId { get; set; }

    /// <summary>Only for reading v1/v2 configs; moved into the first profile by <see cref="Normalize"/>.</summary>
    [JsonPropertyName("buttons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DeckButton>? LegacyButtons { get; set; }

    [JsonIgnore]
    public DeckProfile ActiveProfile
    {
        get
        {
            if (Profiles.Count == 0) EnsureProfiles();
            return Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];
        }
    }

    /// <summary>Buttons of the active profile.</summary>
    [JsonIgnore] public List<DeckButton> Buttons => ActiveProfile.Buttons;

    [JsonIgnore] public int Rows { get => ActiveProfile.Rows; set => ActiveProfile.Rows = value; }
    [JsonIgnore] public int Columns { get => ActiveProfile.Columns; set => ActiveProfile.Columns = value; }

    /// <summary>Buttons of every profile (e.g. to know which icon files are still in use).</summary>
    [JsonIgnore] public IEnumerable<DeckButton> AllButtons => Profiles.SelectMany(p => p.Buttons);

    public void Normalize()
    {
        Settings ??= new DeckSettings();
        Settings.Normalize();
        EnsureProfiles();
        foreach (var p in Profiles) p.Normalize();
        Version = CurrentVersion;
    }

    /// <summary>There is always at least one profile; old configs become the profile "Hlavný".</summary>
    private void EnsureProfiles()
    {
        Profiles ??= new List<DeckProfile>();
        if (Profiles.Count == 0)
        {
            Profiles.Add(new DeckProfile
            {
                Name = DefaultProfileName,
                Rows = Settings?.LegacyRows ?? 3,
                Columns = Settings?.LegacyColumns ?? 5,
                Buttons = LegacyButtons ?? new List<DeckButton>(),
            });
        }
        LegacyButtons = null;
        if (Settings != null) Settings.LegacyRows = Settings.LegacyColumns = null;
        if (ActiveProfileId == null || Profiles.All(p => p.Id != ActiveProfileId)) ActiveProfileId = Profiles[0].Id;
    }

    // ───────────── profiles ─────────────

    public DeckProfile? FindProfile(string? id) => Profiles.FirstOrDefault(p => p.Id == id);

    public DeckProfile AddProfile(string name)
    {
        var profile = new DeckProfile { Name = name, Rows = Rows, Columns = Columns };
        Profiles.Add(profile);
        return profile;
    }

    /// <summary>Deep copy with new ids. Icon files are shared (they are only deleted when no profile uses them).</summary>
    public DeckProfile DuplicateProfile(string id, string name)
    {
        var source = FindProfile(id) ?? ActiveProfile;
        var copy = ConfigStore.Deserialize<DeckProfile>(ConfigStore.Serialize(source));
        copy.Id = DeckProfile.NewId();
        copy.Name = name;
        foreach (var b in copy.Buttons) b.Id = DeckButton.NewId();
        Profiles.Insert(Profiles.IndexOf(source) + 1, copy);
        return copy;
    }

    /// <summary>The last profile can't be removed.</summary>
    public bool RemoveProfile(string id)
    {
        if (Profiles.Count <= 1) return false;
        bool removed = Profiles.RemoveAll(p => p.Id == id) > 0;
        if (ActiveProfileId == id) ActiveProfileId = Profiles[0].Id;
        return removed;
    }

    /// <summary>
    /// Volume and playback buttons in the first row, the first thing a new deck shows.
    /// Cells that are already taken are skipped.
    /// </summary>
    private static readonly (MediaCommand Command, string Sk, string En)[] StarterButtons =
    {
        (MediaCommand.PlayPause, "Play / pauza", "Play / pause"),
        (MediaCommand.Next, "Ďalšia", "Next"),
        (MediaCommand.VolumeDown, "Tichšie", "Volume down"),
        (MediaCommand.VolumeUp, "Hlasnejšie", "Volume up"),
        (MediaCommand.Mute, "Stlmiť", "Mute"),
    };

    /// <summary>Names the app gives by itself; they follow the language, names the user typed don't.</summary>
    private static readonly (string Sk, string En)[] DefaultProfileNames =
    {
        ("Hlavný", "Main"),
        ("Zvuk", "Sound"),
        ("Nový profil", "New profile"),
    };

    /// <summary>
    /// After a language switch: profile names and button labels that the app created itself
    /// ("Hlavný", "Zvuk 2", "Play / pauza", "… (kópia)") are renamed to the other language.
    /// </summary>
    public void TranslateDefaults(AppLanguage to)
    {
        string Pick((string Sk, string En) p) => to == AppLanguage.En ? p.En : p.Sk;
        foreach (var profile in Profiles)
        {
            var name = profile.Name;
            string copySuffix = "";
            foreach (var suffix in new[] { " (kópia)", " (copy)" })
            {
                if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                name = name[..^suffix.Length];
                copySuffix = to == AppLanguage.En ? " (copy)" : " (kópia)";
                break;
            }
            foreach (var pair in DefaultProfileNames)
            {
                foreach (var source in new[] { pair.Sk, pair.En })
                {
                    if (name == source) name = Pick(pair);
                    else if (name.StartsWith(source + " ", StringComparison.Ordinal) && int.TryParse(name[(source.Length + 1)..], out int n))
                        name = $"{Pick(pair)} {n}";
                    else continue;
                    goto renamed;
                }
            }
            renamed:
            profile.Name = name + copySuffix;

            foreach (var button in profile.Buttons)
            {
                if (button.Action is not MediaAction media) continue;
                foreach (var (command, sk, en) in StarterButtons)
                    if (media.Command == command && (button.Label == sk || button.Label == en))
                        button.Label = to == AppLanguage.En ? en : sk;
            }
        }
    }

    public static void AddMediaButtons(DeckProfile profile)
    {
        int col = 0;
        foreach (var (command, sk, en) in StarterButtons)
        {
            while (profile.Buttons.Any(b => b.Row == 0 && b.Col == col)) col++;
            profile.Buttons.Add(new DeckButton
            {
                Row = 0,
                Col = col++,
                Label = L.T(sk, en),
                Action = new MediaAction { Command = command },
            });
        }
        profile.Columns = Math.Max(profile.Columns, Math.Min(col, DeckProfile.MaxColumns));
    }

    /// <summary>A new profile with the media buttons (Profiles → "Sound profile").</summary>
    public DeckProfile AddMediaProfile()
    {
        var profile = AddProfile(UniqueProfileName(L.T("Zvuk", "Sound")));
        AddMediaButtons(profile);
        return profile;
    }

    public string UniqueProfileName(string baseName)
    {
        if (Profiles.All(p => p.Name != baseName)) return baseName;
        for (int i = 2; ; i++)
            if (Profiles.All(p => p.Name != $"{baseName} {i}")) return $"{baseName} {i}";
    }

    // ───────────── buttons of the active profile ─────────────

    public DeckButton? GetAt(int row, int col) =>
        Buttons.FirstOrDefault(b => b.Row == row && b.Col == col);

    public DeckButton? FindById(string id) =>
        Buttons.FirstOrDefault(b => b.Id == id);

    /// <summary>Buttons outside the current grid are kept but hidden (and their hotkeys are inactive).</summary>
    public bool IsVisible(DeckButton b) =>
        b.Row >= 0 && b.Row < Rows && b.Col >= 0 && b.Col < Columns;

    [JsonIgnore]
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
        int total = Rows * Columns;
        int start = Math.Clamp(startRow * Columns + startCol, 0, Math.Max(0, total - 1));
        for (int i = 0; i < total; i++)
        {
            int index = (start + i) % total;
            int r = index / Columns, c = index % Columns;
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

/// <summary>A set of buttons with its own grid size. Switch between them from the header, a button or the phone.</summary>
public sealed class DeckProfile
{
    public const int MinRows = 1, MaxRows = 10;
    public const int MinColumns = 1, MaxColumns = 12;

    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "Profil";
    public int Rows { get; set; } = 3;
    public int Columns { get; set; } = 5;
    public List<DeckButton> Buttons { get; set; } = new();

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = NewId();
        Name = string.IsNullOrWhiteSpace(Name) ? "Profil" : Name.Trim();
        Rows = Math.Clamp(Rows, MinRows, MaxRows);
        Columns = Math.Clamp(Columns, MinColumns, MaxColumns);
        Buttons ??= new List<DeckButton>();
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>Settings shared by all profiles.</summary>
public sealed class DeckSettings
{
    public const int MinButtonSize = 40, MaxButtonSize = 200;
    public const int MinSpacing = 0, MaxSpacing = 40;

    /// <summary>v1/v2 only (now per profile).</summary>
    [JsonPropertyName("rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyRows { get; set; }

    [JsonPropertyName("columns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyColumns { get; set; }

    public int ButtonSize { get; set; } = 80;
    public int Spacing { get; set; } = 8;
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool StartWithWindows { get; set; }

    /// <summary>"sk" or "en"; null until the user picked one (asked on first start).</summary>
    public string? Language { get; set; }
    /// <summary>Look for a newer build on GitHub at startup and every few hours.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public WindowPosition? Window { get; set; }
    public ObsSettings Obs { get; set; } = new();
    public RemoteSettings Remote { get; set; } = new();

    public void Normalize()
    {
        Obs ??= new ObsSettings();
        Obs.Normalize();
        Remote ??= new RemoteSettings();
        Remote.Normalize();
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

/// <summary>Control from a phone: built-in web server on the local network.</summary>
public sealed class RemoteSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8765;

    /// <summary>Secret in the QR code. Changing it disconnects every phone.</summary>
    public string Token { get; set; } = "";

    /// <summary>LAN address shown in the QR code (null = pick automatically).</summary>
    public string? Address { get; set; }

    public void Normalize()
    {
        Port = Port is > 0 and <= 65535 ? Port : 8765;
        Token ??= "";
    }

    public static string NewToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
[JsonDerivedType(typeof(ProfileAction), "profile")]
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

/// <summary>Switches the deck to another profile (like a folder on a hardware Stream Deck).</summary>
public sealed class ProfileAction : DeckAction
{
    public string ProfileId { get; set; } = "";
}
