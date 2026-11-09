using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.App.Views;

public sealed class TicketFilterWindow : Window
{
    public TicketFilter Filter { get; private set; }

    public TicketFilterWindow(TicketFilter initial, IReadOnlyList<string> types)
    {
        Filter = initial;
        Title = "Filter taskbar tickets";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = panel;
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 4) });
        Label("Ticket types (Ctrl-click to select multiple; no selection means all)");
        var typeList = new ListBox { ItemsSource = types, SelectionMode = SelectionMode.Multiple, Height = 90 };
        foreach (var type in initial.Types) typeList.SelectedItems.Add(type);
        panel.Children.Add(typeList);
        Label("Ticket number / key contains");
        var key = new TextBox { Text = initial.Key }; panel.Children.Add(key);
        Label("Summary contains all these words");
        var summary = new TextBox { Text = initial.Summary }; panel.Children.Add(summary);
        Label("Remaining work estimate");
        var estimate = new ComboBox { ItemsSource = new[] { "Any (including missing)", "Has estimate (including zero)", "No estimate" }, SelectedIndex = (int)initial.Estimate };
        panel.Children.Add(estimate);
        Label("Minimum remaining minutes (inclusive; blank means no minimum)");
        var min = new TextBox { Text = initial.MinimumSeconds.HasValue ? (initial.MinimumSeconds.Value / 60m).ToString(CultureInfo.CurrentCulture) : "" }; panel.Children.Add(min);
        Label("Maximum remaining minutes (inclusive; blank means no maximum)");
        var max = new TextBox { Text = initial.MaximumSeconds.HasValue ? (initial.MaximumSeconds.Value / 60m).ToString(CultureInfo.CurrentCulture) : "" }; panel.Children.Add(max);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(error);
        Label("Filters affect the bar only. Your running ticket stays visible.");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(actions);
        var reset = new Button { Content = "Reset all", Padding = new Thickness(10, 4, 10, 4) }; actions.Children.Add(reset);
        reset.Click += (_, _) => { Filter = new TicketFilter(); DialogResult = true; };
        var apply = new Button { Content = "Apply", IsDefault = true, Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) }; actions.Children.Add(apply);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(10, 4, 10, 4) }; actions.Children.Add(cancel);
        apply.Click += (_, _) =>
        {
            bool Parse(string text, out int? seconds)
            {
                seconds = null;
                if (string.IsNullOrWhiteSpace(text)) return true;
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || value < 0 || value > int.MaxValue / 60m) return false;
                seconds = (int)decimal.Round(value * 60);
                return true;
            }
            if (!Parse(min.Text, out var lower) || !Parse(max.Text, out var upper) || lower > upper ||
                (estimate.SelectedIndex == 2 && (lower.HasValue || upper.HasValue)))
            { error.Text = "Enter non-negative minute bounds with minimum ≤ maximum. Clear the bounds when selecting No estimate."; return; }
            Filter = new TicketFilter { Types = typeList.SelectedItems.Cast<string>().ToArray(), Key = key.Text,
                Summary = summary.Text, MinimumSeconds = lower, MaximumSeconds = upper, Estimate = (EstimateFilter)estimate.SelectedIndex };
            DialogResult = true;
        };
    }
}
