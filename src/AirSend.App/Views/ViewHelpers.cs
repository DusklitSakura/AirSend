using Microsoft.UI.Xaml;

namespace AirSend.Views;

/// <summary>
/// Static helpers bound from XAML with <c>x:Bind</c>. Using functions keeps the
/// markup free of converters (<c>Converter={x:Null}</c> crashes x:Bind at runtime).
/// </summary>
public static class ViewHelpers
{
    public static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToInverseVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Visible(string? value) =>
        string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

}
