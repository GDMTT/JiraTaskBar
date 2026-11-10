# Taskbar filters

Click **Filter…** on the bar to select ticket types from a compact multi-select dropdown,
a partial ticket key, summary words, due-date state, and inclusive time-until-due bounds.
Types are alternatives; all other filters combine with AND. Every summary word must occur,
ignoring case.

Durations use Jira notation such as `1w 2d 3h 30m`. Day and week units use the connected
site's configured working hours per day and working days per week when Jira makes those
values available (otherwise 8 hours and 5 days). A due date means local midnight at the
end of that date, matching the taskbar due-date indicator. A numeric bound excludes tickets
without due dates. **No due date** requires empty bounds, while **Overdue** selects deadlines
that have passed. **Reset all** restores the unfiltered Jira query. Filters last for this app session.

The **Filters ●** label indicates active filters. A running ticket remains visible,
dimmed when excluded, until stopped. Filtering never changes Jira or stops tracking.
Time-dependent matches are recalculated once per minute while the app is running.
