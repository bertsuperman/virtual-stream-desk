using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.App.UI;

/// <summary>Message boxes (Avalonia has none built in).</summary>
internal static class Dialogs
{
    public static Task<bool> ConfirmAsync(Window? owner, string title, string text, bool defaultYes = false) =>
        ShowAsync(owner, title, text, L.T("Áno", "Yes"), L.T("Nie", "No"), defaultYes);

    public static Task InfoAsync(Window? owner, string title, string text) =>
        ShowAsync(owner, title, text, "OK", null, true);

    /// <summary>Builds the dialog without showing it (also used for screenshots).</summary>
    public static Window Build(string title, string text, string yes, string? no, bool defaultYes, System.Action<bool>? onResult = null)
    {
        var window = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Ui.PanelBg,
            Topmost = true,
        };

        void Answer(bool value)
        {
            onResult?.Invoke(value);
            window.Close(value);
        }

        var yesButton = defaultYes ? Ui.AccentButton(yes, () => Answer(true), 90) : Ui.Button(yes, () => Answer(true), 90);
        yesButton.IsDefault = defaultYes;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (no != null)
        {
            var noButton = defaultYes ? Ui.Button(no, () => Answer(false), 90) : Ui.AccentButton(no, () => Answer(false), 90);
            noButton.IsDefault = !defaultYes;
            noButton.IsCancel = true;
            buttons.Children.Add(yesButton);
            buttons.Children.Add(noButton);
        }
        else
        {
            yesButton.IsCancel = true;
            buttons.Children.Add(yesButton);
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 20),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Label, LineHeight = 20 },
                buttons,
            },
        };
        return window;
    }

    private static async Task<bool> ShowAsync(Window? owner, string title, string text, string yes, string? no, bool defaultYes)
    {
        bool result = false; // closing the window = "No"
        var window = Build(title, text, yes, no, defaultYes, r => result = r);
        if (owner is { IsVisible: true })
        {
            await window.ShowDialog(owner);
            return result;
        }

        var tcs = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => tcs.TrySetResult(result);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        window.Activate();
        return await tcs.Task;
    }
}

/// <summary>First start: "Slovenčina or English?" Closing the window keeps the Windows/macOS language.</summary>
internal sealed class LanguageWindow : Window
{
    private AppLanguage _choice = L.FromSystem();

    public LanguageWindow()
    {
        Title = AppInfo.ProductName;
        Background = Ui.PanelBg;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var sk = Choice("Slovenčina", AppLanguage.Sk);
        var en = Choice("English", AppLanguage.En);
        Content = new StackPanel
        {
            Margin = new Thickness(32, 26, 32, 28),
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "Vyber jazyk  ·  Choose a language",
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Ui.Label,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "Neskôr ho zmeníš v Nastavenia → Všeobecné.\nYou can change it later in Settings → General.",
                    FontSize = 12,
                    Foreground = Ui.Muted,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 14),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { sk, en },
                },
            },
        };
    }

    private Button Choice(string text, AppLanguage language)
    {
        var button = new Button
        {
            Content = text,
            Width = 160,
            Height = 54,
            FontSize = 16,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsDefault = language == _choice, // Enter picks the system language
        };
        if (language == _choice) button.Classes.Add("accent");
        button.Click += (_, _) =>
        {
            _choice = language;
            Close();
        };
        return button;
    }

    public static async Task<AppLanguage> AskAsync()
    {
        var window = new LanguageWindow();
        var tcs = new TaskCompletionSource<AppLanguage>();
        window.Closed += (_, _) => tcs.TrySetResult(window._choice);
        window.Show();
        window.Activate();
        return await tcs.Task;
    }
}
