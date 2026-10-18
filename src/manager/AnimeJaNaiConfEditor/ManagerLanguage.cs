using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using AnimeJaNai.Localization;
namespace AnimeJaNaiConfEditor;
public sealed class TextExtension
{
    public string Key { get; set; } = "";
    public string ProvideValue(IServiceProvider provider) => UiText.Key(Key);
}
public sealed class LocalizedFormatConverter : IValueConverter
{
    public static readonly LocalizedFormatConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.Format(culture,UiText.Key((string)parameter!),value);
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Avalonia.AvaloniaProperty.UnsetValue;
}
public sealed class LocalizedTextConverter : IValueConverter
{
    public static readonly LocalizedTextConverter Instance = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c) => UiText.T(value?.ToString());
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Avalonia.AvaloniaProperty.UnsetValue;
}
public static class LanguagePanel
{
    public static void Attach(Window window)
    {
        var select = window.FindControl<ComboBox>("InterfaceLanguageSelector")
            ?? throw new InvalidOperationException("Language selector missing from Global Settings.");
        var hint = window.FindControl<TextBlock>("InterfaceLanguageHint")
            ?? throw new InvalidOperationException("Language hint missing from Global Settings.");
        select.ItemsSource = new[]{"简体中文","English","跟随系统 / System"};
        select.SelectedIndex = Array.IndexOf(InterfaceLanguage.Choices, InterfaceLanguage.Read());
        hint.Text = UiText.T("Restart the player and manager to apply language changes.");
        select.SelectionChanged += (_,_) => {
            if (select.SelectedIndex < 0) return;
            try {
                InterfaceLanguage.Save(InterfaceLanguage.Choices[select.SelectedIndex]);
                hint.Text = UiText.T("Saved. Restart the player and manager to apply.");
            }
            catch (Exception e) {
                hint.Text = UiText.T("Could not save language: ") + e.Message;
            }
        };
    }
}
