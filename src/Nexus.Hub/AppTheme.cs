using System.Windows;
using System.Windows.Controls;

namespace Nexus.Hub;

/// <summary>
/// Light or dark: follows Windows, or a choice saved in the hub settings. Every window (the hub, its dialogs and
/// the windows built in code) takes its text colour from the theme: the Windows 11 theme colours controls but
/// leaves a window's own text at the system default (black), which is unreadable on the dark background.
/// </summary>
internal static class AppTheme
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    private static bool _hooked;

    /// <summary>Applies the theme (null or unknown: follow Windows) and makes sure window text follows it.</summary>
    public static void Apply(string? choice)
    {
        if (!_hooked)
        {
            _hooked = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        }
#pragma warning disable WPF0001 // ThemeMode is marked experimental in .NET 10
        Application.Current.ThemeMode = choice switch
        {
            Light => ThemeMode.Light,
            Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
        foreach (Window window in Application.Current.Windows) FollowTheme(window);
    }

    public static string Normalize(string? choice) => choice is Light or Dark ? choice : System;

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) FollowTheme(window);
    }

    private static void FollowTheme(Window window)
    {
        // Only when the window did not pick its own colour. A resource reference follows later theme changes.
        if (window.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue)
            window.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
    }
}
