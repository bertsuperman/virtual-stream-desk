using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.App;

/// <summary>First start: "Slovenčina or English?" Closing the window keeps the Windows language.</summary>
internal sealed class LanguageWindow : Window
{
    private Language _choice = L.FromSystem();

    private LanguageWindow()
    {
        Title = AppInfo.ProductName;
        ThemeMode = ThemeMode.Dark;
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x21));
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 26) };
        panel.Children.Add(new TextBlock
        {
            Text = "Vyber jazyk  ·  Choose a language",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Neskôr ho zmeníš v Nastavenia → Všeobecné.\nYou can change it later in Settings → General.",
            FontSize = 12,
            Opacity = 0.65,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(Choice("Slovenčina", Language.Sk, new Thickness(0, 0, 12, 0)));
        buttons.Children.Add(Choice("English", Language.En, new Thickness(0)));
        panel.Children.Add(buttons);
        Content = panel;
    }

    private Button Choice(string text, Language language, Thickness margin)
    {
        var button = new Button
        {
            Content = text,
            Width = 150,
            Height = 52,
            FontSize = 16,
            Margin = margin,
            IsDefault = language == _choice, // Enter picks the Windows language
        };
        button.Click += (_, _) =>
        {
            _choice = language;
            Close();
        };
        return button;
    }

    public static Language Ask()
    {
        var window = new LanguageWindow();
        window.ShowDialog();
        return window._choice;
    }
}
