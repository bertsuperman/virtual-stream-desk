namespace VirtualStreamDeck.Core;

public enum Language { Sk, En }

/// <summary>
/// UI language. Every text is written as a pair right where it is used: <c>L.T("Slovensky", "English")</c>,
/// so a translation can never go missing or drift away from its original.
/// </summary>
public static class L
{
    private static volatile Language _current = Language.Sk;

    public static Language Current => _current;

    /// <summary>Raised after <see cref="Set"/> changed the language (on the caller's thread).</summary>
    public static event Action? Changed;

    public static void Set(Language language)
    {
        if (_current == language) return;
        _current = language;
        Changed?.Invoke();
    }

    public static string T(string sk, string en) => _current == Language.En ? en : sk;

    /// <summary>"sk" / "en" as stored in config.json.</summary>
    public static string Code(Language language) => language == Language.En ? "en" : "sk";

    public static Language? Parse(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "sk" => Language.Sk,
        "en" => Language.En,
        _ => null,
    };

    /// <summary>Slovak for sk/cs Windows, English otherwise (the preselected choice on first start).</summary>
    public static Language FromSystem() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "sk" or "cs"
            ? Language.Sk
            : Language.En;
}
