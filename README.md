# Jira TaskBar

A small always-on-top bar with one button per Jira ticket for one-click time tracking. It is built with C# / .NET 10 and WPF + [WPF-UI](https://github.com/lepoco/wpfui) (Fluent).

## Using it

| Action | Result |
|---|---|
| **Click** a ticket | Starts its timer. Clicking another ticket switches to it, and clicking the running ticket stops it. Each stop posts a Jira worklog. |
| **Hover** | Shows the work-type icon and key, then the title, type, priority and status. |
| **Right-click** | Opens ticket actions, including adding a comment and reviewing its worklogs. |
| **Middle-click** or the ↗ icon | Opens the ticket in your browser. |
| **Drag the grip** (top/left dots) | Moves the bar. Dropping it near any screen edge docks it, and other windows make room like they do for the taskbar. Dragging it away undocks it. |
| **⋯ menu / tray icon** | Refresh, stop timer, dock (Floating / Left / Top / Right / Bottom), show/hide, settings, start with Windows, exit. |

By default the bar, its tooltips and its menus are **hidden from screen sharing, screenshots and recordings**. You still see them, but people on a Teams or Zoom share don't. You can turn this off in Settings, or set the bar to show only ticket keys.

Each button shows the ticket's **work-type icon**, the same one Jira shows, including custom types:
- Icons are fetched once per session from your Jira site. A scoped token fetches them through the API gateway.
- Only real PNG images are used. If an icon can't be loaded, for example because a scoped token isn't allowed to read avatars, the button shows a matching built-in glyph instead (Incident, Change, Problem, Service request and so on).

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

### Time review and context switching
- Choose **View time calendar…** from a ticket's right-click menu to review saved Jira worklogs and clearly marked pending local worklogs in Month, Week, or Day view.
- Set the calendar resolution to 5, 10, 15, 30, or 60 minutes in Settings. Day/Week selections, moves, and duration changes snap to that interval.
- Drag across an empty Day/Week time range, click **New worklog…**, or drag a ticket from JiraTaskBar onto the calendar to create a worklog. Its description can be stored on the worklog or posted as a safe internal ticket comment.
- Click a saved worklog to review or edit its time, duration, and description. The calendar keeps its scroll position after edits and refreshes.
- Drag a saved worklog to move it within the week; drag its bottom handle to change duration. Jira is checked for a newer version before saving. Pending local worklogs remain read-only until Jira accepts them.
- Choose **Context switching…** from the application or tray menu to review uninterrupted intervals, active time, ticket totals, and direct ticket-to-ticket transitions for a date range.
- Context history stays in `%LOCALAPPDATA%\JiraTaskBar\context-switch-history.json`; it is never sent to Jira or an analytics service. It is retained until you use **Delete local history…** in the report.

## Sign-in
Jira Cloud uses your Atlassian email and an [API token](https://id.atlassian.com/manage-profile/security/api-tokens). Only `https://<name>.atlassian.net` sites are accepted.

**Use a scoped token (recommended).** On the token page choose **Create API token with scopes** → **Jira**, and select `read:jira-work`, `write:jira-work` and `read:jira-user`. A leaked scoped token only exposes Jira work items, not your whole Atlassian account. Scoped tokens only work through Atlassian's API gateway, `api.atlassian.com/ex/jira/<cloud id>`, so the app needs your site's cloud ID:
- Settings looks it up automatically from `https://<site>/_edge/tenant_info`, without sending your token.
- That endpoint isn't officially supported, so if the lookup fails, paste the cloud ID yourself. On admin.atlassian.com it's the value after `/s/` in the address bar.

A classic token still works, but Settings warns that it has full access to your account.

The site, email and token are stored together as a Generic Credential in Windows Credential Manager. Editing other files therefore can't redirect the token to a different site. Existing `credentials.bin` DPAPI data is migrated automatically and removed only after the new credential is verified. If you change the site in Settings, you must enter the token again.

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
This runs a locked restore and the tests, publishes a self-contained single exe to `artifacts/publish`, optionally signs it, starts the packaged exe in smoke-test mode, and writes a SHA-256 file next to it. The smoke test must exit successfully within 30 seconds and does not require Jira credentials or network access. Without a certificate it builds unsigned and warns. Install the result under `%ProgramFiles%` so other processes can't replace the exe.

GitHub Actions runs locked restore, warning-free Release build, and all tests for pushes and pull requests. On its clean Windows runner it also starts the packaged executable in smoke-test mode, then uploads the unsigned `JiraTaskBar.exe` and SHA-256 checksum as a workflow artifact. Pushing a semantic-version tag (for example `v1.1.0` or `v1.2.0-beta.1`) runs the release workflow, stamps that version into the executable, generates release notes, and attaches the executable and checksum to a GitHub Release. Tags containing a prerelease suffix create a GitHub prerelease. Use **Run workflow** on the Actions page to test packaging without creating a release.

To publish a release from an up-to-date `main` branch:

```powershell
git tag -a v1.1.0 -m "Jira TaskBar v1.1.0"
git push origin v1.1.0
```

CI binaries are unsigned until a managed code-signing certificate is configured. Treat the workflow artifact as a test build rather than a production installer until signing is enabled.

### Package supply chain
- `nuget.config` uses nuget.org only, with package source mapping, and requires nuget.org's repository signature on every package. The machine-level NuGet config has no sources; if IT mandates an internal feed, switch to it here.
- `packages.lock.json` pins exact versions and hashes. After changing a package, run `dotnet restore --force-evaluate` and commit the updated lock files.
- NuGet Audit checks every package, including indirect ones, on each restore. High and critical advisories fail the build.

## Layout
- `src/JiraTaskBar.Core`: the Jira REST client, the time tracker, the worklog retry queue, settings/secret storage, and the wiki-markup preview. It has no UI code.
- `src/JiraTaskBar.App`: the WPF app (bar, AppBar docking interop, comment and settings windows, tray).
- `tests/`: unit tests for Core, plus a smoke test that loads every window with the real XAML resources.

## License

Jira TaskBar is licensed under the [GNU General Public License v3.0 only](LICENSE) (`GPL-3.0-only`).

You may use, study, modify and redistribute the program. If you distribute the program or a modified version, you must provide the corresponding source under GPLv3, preserve the license and notices, and license the combined work under GPLv3. Private use and internal modifications do not require publication. GPLv3 includes an express patent grant and disclaims warranty; it does not grant rights to the Jira, Atlassian or third-party dependency trademarks.
