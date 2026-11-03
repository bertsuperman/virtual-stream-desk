using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.App;

/// <summary>
/// XAML text in both languages: <c>Text="{l:T 'Profily', 'Profiles'}"</c>.
/// It is a binding, so switching the language updates every open window right away.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string sk, string en)
    {
        Sk = sk;
        En = en;
    }

    [ConstructorArgument("sk")] public string Sk { get; set; } = "";
    [ConstructorArgument("en")] public string En { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LanguageSource.Language))
        {
            Source = LanguageSource.Instance,
            Mode = BindingMode.OneWay,
            Converter = new Pick(Sk, En),
        }.ProvideValue(serviceProvider);

    private sealed class Pick(string sk, string en) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is AppLanguage.En ? en : sk;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

/// <summary>Bindable view of <see cref="L.Current"/>.</summary>
public sealed class LanguageSource : INotifyPropertyChanged
{
    public static LanguageSource Instance { get; } = new();

    private LanguageSource()
    {
        L.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    public AppLanguage Language => L.Current;

    public event PropertyChangedEventHandler? PropertyChanged;
}
