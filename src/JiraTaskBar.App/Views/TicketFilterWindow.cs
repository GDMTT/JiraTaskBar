using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.App.Views;

public sealed class TicketFilterWindow : Window
{
    public TicketFilter Filter { get; private set; }

    public TicketFilterWindow(TicketFilter initial, IReadOnlyList<string> types, JiraDurationSettings? durationSettings = null)
    {
        var durations = durationSettings ?? JiraDurationSettings.Default;
        Filter = initial;
        Title = "Filter taskbar tickets";
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        Width = 460;
        MaxHeight = SystemParameters.WorkArea.Height;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        TextBlock? lastLabel = null;
        void Label(string text)
        {
            lastLabel = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
            panel.Children.Add(lastLabel);
        }
        void AddField(Control field)
        {
            System.Windows.Automation.AutomationProperties.SetLabeledBy(field, lastLabel);
            panel.Children.Add(field);
        }
        Label("Ticket types (no selection means all)");
        var selectedTypes = new HashSet<string>(initial.Types, StringComparer.OrdinalIgnoreCase);
        var typeButton = new ToggleButton { HorizontalContentAlignment = HorizontalAlignment.Left };
        string TypeSummary() => selectedTypes.Count switch
        {
            0 => "All ticket types",
            1 => selectedTypes.Single(),
            <= 3 => string.Join(", ", selectedTypes.Order(StringComparer.OrdinalIgnoreCase)),
            _ => $"{selectedTypes.Count} ticket types selected",
        };
        typeButton.Content = TypeSummary();
        AddField(typeButton);
        var typePanel = new StackPanel { MinWidth = 360, Background = SystemColors.WindowBrush };
        foreach (var type in types)
        {
            var check = new CheckBox
            {
                Content = type,
                IsChecked = selectedTypes.Contains(type),
                Margin = new Thickness(6, 3, 6, 3),
                Foreground = SystemColors.WindowTextBrush,
            };
            check.Checked += (_, _) => { selectedTypes.Add(type); typeButton.Content = TypeSummary(); };
            check.Unchecked += (_, _) => { selectedTypes.Remove(type); typeButton.Content = TypeSummary(); };
            typePanel.Children.Add(check);
        }
        var typePopup = new Popup
        {
            PlacementTarget = typeButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = SystemColors.ActiveBorderBrush,
                Padding = new Thickness(4),
                Background = SystemColors.WindowBrush,
                Child = new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = typePanel },
            },
        };
        panel.Children.Add(typePopup);
        typeButton.Checked += (_, _) => typePopup.IsOpen = true;
        typeButton.Unchecked += (_, _) => typePopup.IsOpen = false;
        typePopup.Closed += (_, _) => typeButton.IsChecked = false;
        Label("Ticket number / key contains");
        var key = new TextBox { Text = initial.Key }; AddField(key);
        Label("Summary contains all these words");
        var summary = new TextBox { Text = initial.Summary }; AddField(summary);
        Label("Due date");
        var dueDate = new ComboBox
        {
            ItemsSource = new[] { "Any (with or without a due date)", "Has a due date", "No due date", "Overdue" },
            SelectedIndex = (int)initial.DueDate,
        };
        AddField(dueDate);
        Label("Minimum time until due date (inclusive; e.g. 1d 2h; blank means no minimum)");
        var initialMinText = JiraDuration.Format(initial.MinimumDueSeconds, durations);
        var min = new TextBox { Text = initialMinText }; AddField(min);
        Label("Maximum time until due date (inclusive; e.g. 30m; blank means no maximum)");
        var initialMaxText = JiraDuration.Format(initial.MaximumDueSeconds, durations);
        var max = new TextBox { Text = initialMaxText }; AddField(max);
        panel.Children.Add(new TextBlock
        {
            Text = $"Jira duration units use {durations.HoursPerDay:g} hours per day and {durations.DaysPerWeek:g} days per week. Due dates end at local midnight after the selected date.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 4, 0, 0),
        });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(error);
        Label("Filters affect the bar only. Your running ticket stays visible.");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(actions);
        var reset = new Button { Content = "Reset all", Padding = new Thickness(10, 4, 10, 4) }; actions.Children.Add(reset);
        reset.Click += (_, _) => { Filter = new TicketFilter(); DialogResult = true; };
        var apply = new Button { Content = "Apply", IsDefault = true, Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) }; actions.Children.Add(apply);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(10, 4, 10, 4) }; actions.Children.Add(cancel);
        apply.Click += (_, _) =>
        {
            if (!JiraDuration.TryParsePreservingOriginal(min.Text, initialMinText, initial.MinimumDueSeconds, durations, out var lower) ||
                !JiraDuration.TryParsePreservingOriginal(max.Text, initialMaxText, initial.MaximumDueSeconds, durations, out var upper) || lower > upper ||
                ((dueDate.SelectedIndex == (int)DueDateFilter.NoDueDate ||
                  dueDate.SelectedIndex == (int)DueDateFilter.Overdue) && (lower.HasValue || upper.HasValue)))
            {
                error.Text = "Enter Jira durations such as 1w 2d 3h 30m with minimum ≤ maximum. Clear the bounds when selecting No due date or Overdue.";
                return;
            }
            Filter = new TicketFilter { Types = selectedTypes.Order(StringComparer.OrdinalIgnoreCase).ToArray(), Key = key.Text,
                Summary = summary.Text, MinimumDueSeconds = lower, MaximumDueSeconds = upper, DueDate = (DueDateFilter)dueDate.SelectedIndex };
            DialogResult = true;
        };
    }
}
