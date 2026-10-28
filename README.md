# Jira TaskBar

A small always-on-top bar with one button per Jira ticket for one-click time tracking. It is built with C# / .NET 10 and WPF + [WPF-UI](https://github.com/lepoco/wpfui) (Fluent).

## Using it

| Action | Result |
|---|---|
| **Click** a ticket | Starts its timer. Clicking another ticket switches to it, and clicking the running ticket stops it. Each stop posts a Jira worklog. |
| **Hover** | Shows the key, title, type, priority and status. |
| **Right-click** | Opens the comment editor. |
| **Middle-click** or the ↗ icon | Opens the ticket in your browser. |
| **Drag the grip** (top/left dots) | Moves the bar. Dropping it near any screen edge docks it, and other windows make room like they do for the taskbar. Dragging it away undocks it. |
| **⋯ menu / tray icon** | Refresh, stop timer, dock (Floating / Left / Top / Right / Bottom), show/hide, settings, start with Windows, exit. |

Buttons are sorted by Jira priority (priority 1 first) and then by key. The coloured strip on each button shows its priority. A ⚠ icon means some worklogs couldn't be posted yet. They are retried automatically, or you can click the icon to retry or discard them.

### Comment editor
- Supports Jira wiki markup (`*bold*`, `_italic_`, `h3.`, `*` / `#` lists, `{code}`, `[text|url]`, tables `||h||` / `|c|`). There's a toolbar, Ctrl+B/I/U/K shortcuts, and a **Preview** tab.
- **Tables**: use the table picker, or paste cells copied from Excel, which become a Jira table automatically.
- **Images**: paste a screenshot (Ctrl+V), drag and drop files, or use the image/attach buttons. Files are uploaded as attachments when you post and are referenced as `!name.png|thumbnail!`.
- On Jira Service Management tickets you can choose **Internal note** (the default) or **Reply to customer**. A public reply shows a warning banner and asks for confirmation before it is sent.
- Ctrl+Enter posts the comment.

### Time-tracking safeguards
- The running timer is saved to disk. If the app or PC crashes, you're asked on the next start whether to log the time up to when the app was last seen, keep the timer running, or discard it.
- If the PC is locked or asleep longer than a set time (10 min by default), you're asked whether to keep that time, remove it, or stop the timer at the moment you left.
- Sessions under a minute are logged as 1 minute or skipped, depending on a setting.

## Sign-in
Jira Cloud uses your Atlassian email and an [API token](https://id.atlassian.com/manage-profile/security/api-tokens). Only `https://<name>.atlassian.net` sites are accepted.

The site, email and token are encrypted together with Windows DPAPI for your account, in `credentials.bin`. Editing other files therefore can't redirect the token to a different site. If you change the site in Settings, you must enter the token again.

If Jira rejects the token (for example because it expired), the app asks once for a new one. Automatic refreshes pause until you enter it, and worklogs are kept and posted afterwards. Preferences, state and logs are stored in `%LOCALAPPDATA%\JiraTaskBar`.

The ticket list comes from a JQL query you can edit. The default is:
`assignee = currentUser() AND statusCategory != Done ORDER BY priority DESC, key ASC`

## Build & run
```
dotnet build
dotnet test
dotnet run --project src/JiraTaskBar.App
```
A self-contained single exe:
```
dotnet publish src/JiraTaskBar.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
The comment preview uses the Microsoft Edge WebView2 runtime, which comes with Windows 11.

### Releases
```
./build/publish.ps1 -CertificateThumbprint <code-signing cert thumbprint>
```
This runs a locked restore and the tests, then publishes a self-contained single exe to `artifacts/publish`, signs it, and writes a SHA-256 file next to it. Without a certificate it builds unsigned and warns. Install the result under `%ProgramFiles%` so other processes can't replace the exe.

### Package supply chain
- `nuget.config` uses nuget.org only, with package source mapping, and requires nuget.org's repository signature on every package. The machine-level NuGet config has no sources; if IT mandates an internal feed, switch to it here.
- `packages.lock.json` pins exact versions and hashes. After changing a package, run `dotnet restore --force-evaluate` and commit the updated lock files.
- NuGet Audit checks every package, including indirect ones, on each restore. High and critical advisories fail the build.

## Layout
- `src/JiraTaskBar.Core`: the Jira REST client, the time tracker, the worklog retry queue, settings/secret storage, and the wiki-markup preview. It has no UI code.
- `src/JiraTaskBar.App`: the WPF app (bar, AppBar docking interop, comment and settings windows, tray).
- `tests/`: unit tests for Core, plus a smoke test that loads every window with the real XAML resources.
