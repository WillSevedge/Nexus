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

    /// <summary>
    /// "Revisions on sheets": one checkbox per revision, ticked when on all selected sheets, a dash when on
    /// some. Returns only the revisions the user changed (true = show on every selected sheet).
    /// </summary>
    public static Dictionary<string, bool>? AskRevisions(Window owner, int sheetCount,
        IReadOnlyList<(string ColumnId, string Label, bool? State, int Locked)> revisions)
    {
        var boxes = new List<(string Id, bool? Initial, CheckBox Box)>();
        var list = new StackPanel();
        foreach (var (id, label, state, locked) in revisions)
        {
            var box = new CheckBox
            {
                Content = locked > 0 ? $"{label}   (fixed by revision clouds on {locked} sheet{(locked == 1 ? "" : "s")})" : label,
                IsThreeState = state is null,
                IsChecked = state,
                Margin = new Thickness(0, 3, 0, 3),
            };
            // After the first click a mixed box is a plain on/off choice.
            box.Click += (_, _) => box.IsThreeState = false;
            boxes.Add((id, state, box));
            list.Children.Add(box);
        }
        var body = new StackPanel();
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var hint = new TextBlock
        {
            Text = "Ticked: shown on every selected sheet. Dash: on some of them (left as is unless you change it). " +
                   "Revisions placed by revision clouds stay on their sheets, as in Revit.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 10, 0, 0),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        body.Children.Add(hint);

        var window = Shell(owner, "Revisions on sheets",
            $"Revisions shown on the {sheetCount} selected sheet{(sheetCount == 1 ? "" : "s")}:", body, out var ok);
        if (window.ShowDialog() != true || !ok()) return null;

        var changes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (id, initial, box) in boxes)
            if (box.IsChecked is bool now && now != initial) changes[id] = now;
        return changes;
    }

    public static void ShowMessages(Window owner, IEnumerable<string> issues, IEnumerable<string> log, IEnumerable<string>? editing = null)
    {
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Last load", Content = new ListBox { ItemsSource = issues.DefaultIfEmpty("No errors or warnings.").ToList() } });
        if (editing is not null)
            tabs.Items.Add(new TabItem { Header = "Editing", Content = new ListBox { ItemsSource = editing.ToList(), FontFamily = new FontFamily("Consolas") } });
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
