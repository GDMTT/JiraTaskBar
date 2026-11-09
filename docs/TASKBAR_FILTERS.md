# Taskbar filters

Click **Filter…** on the bar to select ticket types, a partial ticket key, summary words,
and remaining-estimate bounds in minutes. Types are alternatives; all other filters
combine with AND. Every summary word must occur, ignoring case.

Bounds are inclusive. A missing estimate differs from an explicit zero. A numeric
bound excludes tickets without estimates. **No estimate** requires empty bounds.
**Reset all** restores the unfiltered Jira query. Filters last for this app session.

The **Filters ●** label indicates active filters. A running ticket remains visible,
dimmed when excluded, until stopped. Filtering never changes Jira or stops tracking.
