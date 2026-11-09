# Due-date indicator

The taskbar pie shows the remaining share of the interval from Jira's creation instant
to the end of its date-only due date. The deadline is the following local midnight,
resolved through the computer's local timezone, including daylight-saving transitions.
The tooltip names that timezone and displays days, hours, minutes and seconds remaining.

Blue = upcoming; amber **S** = due within 48 hours; a red pie = due today (one day
or less); red **!** = overdue. A grey dash means no due date. No valid creation baseline means
an outlined circle with no percentage. The deadline tooltip remains available.

The regular taskbar tick updates the countdown. Jira refreshes replace changed due dates.
This icon does not represent remaining work estimate and never changes time tracking.
