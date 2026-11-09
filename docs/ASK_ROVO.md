# Ask Rovo (browser handoff)

Right-click a ticket → **Ask Rovo…**. Edit the question, choose **Copy question**, then
**Open ticket in Jira**. In Jira's top navigation, select **Ask Rovo**, paste the prompt,
and submit it yourself. Read the answer in Jira; follow its source links to verify it.
The copied prompt contains the selected ticket key, browse URL, question and a read-only
instruction. It never contains the API token, email, or API gateway URL.

## Supported integration and limits (checked 2026-11-09)

Atlassian documents opening Rovo Chat within Jira and adding links as prompt context:

- https://support.atlassian.com/rovo/docs/accessing-chat/
- https://support.atlassian.com/rovo/docs/using-rovo-chat/

The documented programmatic `rovo.open` bridge is for Forge Jira/Confluence modules:
https://developer.atlassian.com/platform/forge/apis-reference/ui-api-bridge/rovo/

This native Windows app uses the documented browser experience. It does not assume a
public native chat endpoint or an undocumented prompt URL. It does not open the Rovo
sidebar automatically, submit questions, scrape responses, or execute suggested actions.

## Setup and troubleshooting

Use a Jira Cloud site and sign into Jira in your normal browser account. Rovo availability
depends on that site's configuration, account access, plan and credits; ask your admin if
the Ask Rovo control is absent or disabled. The API token used for time tracking does not
log your browser into Jira or grant Rovo permissions. Server/Data Center is not supported.

If copying fails, retry after another app releases the clipboard. If the browser fails to
open, copy the displayed ticket URL. Browser network, sign-in and permission errors must
be resolved in Jira. Review the prompt before submitting. Timer state is unaffected.
