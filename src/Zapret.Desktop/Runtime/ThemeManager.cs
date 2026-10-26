using System.Windows;
using Microsoft.Win32;

namespace Zapret.Desktop.Runtime;

// Swaps the palette dictionary (Themes/Dark.xaml or Themes/Light.xaml). Everything in XAML refers to it dynamically;
// code that copies a brush out of the resources listens to Changed and reapplies it.
internal static class ThemeManager
{
    internal const string Dark = "Dark", Light = "Light", FollowSystem = "System";
    internal static string Preference { get; private set; } = Dark;
    internal static string Current { get; private set; } = Dark;
    internal static event Action? Changed;

    internal static string Normalize(string? preference) => preference is Light or FollowSystem ? preference : Dark;

    internal static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    internal static void Apply(string? preference)
    {
        Preference = Normalize(preference);
        string theme = Preference == FollowSystem ? (SystemPrefersLight() ? Light : Dark) : Preference;
        if (theme == Current) return;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"/x2zapret;component/Themes/{theme}.xaml", UriKind.Relative) };
        if (dictionaries.Count == 0) dictionaries.Add(palette); else dictionaries[0] = palette;
        Current = theme;
        Changed?.Invoke();
    }

    // Following Windows: re-evaluate when the user switches the app mode in Settings → Personalisation.
    internal static void Follow() => SystemEvents.UserPreferenceChanged += (_, e) =>
    {
        if (e.Category == UserPreferenceCategory.General && Preference == FollowSystem)
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(FollowSystem));
    };
}
