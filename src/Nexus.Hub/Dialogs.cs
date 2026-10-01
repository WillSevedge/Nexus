using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nexus.Hub;

/// <summary>Small code-built dialogs: ask for a value, find and replace, and the messages window.</summary>
internal static class Dialogs
{
    public static string? AskValue(Window owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 380, Padding = new Thickness(4, 3, 4, 3) };
        var window = Shell(owner, title, prompt, box, out var ok);
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true && ok() ? box.Text : null;
    }

    public sealed record FindReplace(string Find, string Replace, bool MatchCase, bool WholeCell, bool SelectionOnly);

    public static FindReplace? AskFindReplace(Window owner, bool hasSelection)
    {
        var find = new TextBox { MinWidth = 320, Padding = new Thickness(4, 3, 4, 3) };
        var replace = new TextBox { MinWidth = 320, Padding = new Thickness(4, 3, 4, 3) };
        var matchCase = new CheckBox { Content = "Match case", Margin = new Thickness(0, 8, 0, 0) };
        var whole = new CheckBox { Content = "Whole cell only", Margin = new Thickness(0, 4, 0, 0) };
        var selection = new CheckBox { Content = "Only in the selected cells", IsChecked = hasSelection, IsEnabled = hasSelection, Margin = new Thickness(0, 4, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Find" });
        panel.Children.Add(find);
        panel.Children.Add(new TextBlock { Text = "Replace with", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(replace);
        panel.Children.Add(matchCase);
        panel.Children.Add(whole);
        panel.Children.Add(selection);
        var window = Shell(owner, "Find and replace", "Replaces text in editable cells; the changes are pending until you apply them.", panel, out var ok);
        window.Loaded += (_, _) => find.Focus();
        if (window.ShowDialog() != true || !ok() || find.Text.Length == 0) return null;
        return new FindReplace(find.Text, replace.Text, matchCase.IsChecked == true, whole.IsChecked == true, selection.IsChecked == true);
    }

    public static void ShowMessages(Window owner, IEnumerable<string> issues, IEnumerable<string> log)
    {
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Last load", Content = new ListBox { ItemsSource = issues.DefaultIfEmpty("No errors or warnings.").ToList() } });
        tabs.Items.Add(new TabItem { Header = "Log", Content = new ListBox { ItemsSource = log.Reverse().ToList(), FontFamily = new FontFamily("Consolas") } });
        new Window
        {
            Title = "Nexus - Messages",
            Owner = owner,
            Width = 900,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(10), Child = tabs },
        }.ShowDialog();
    }

    private static Window Shell(Window owner, string title, string prompt, UIElement body, out Func<bool> accepted)
    {
        bool ok = false;
        var window = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        okButton.Click += (_, _) => { ok = true; window.DialogResult = true; };
        buttons.Children.Add(okButton);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        panel.Children.Add(buttons);
        window.Content = panel;
        accepted = () => ok;
        return window;
    }
}
