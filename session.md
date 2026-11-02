# Session summary — JiraTaskBar

A record of the Claude Code session that built JiraTaskBar. JiraTaskBar is a Windows desktop bar for tracking time against Jira tickets, replacing Jira Assistant.

## Goal

A floating or dockable bar with one button per Jira ticket:
- At most one button is active at a time. The active ticket's timer runs until the button is clicked again or another ticket is chosen. Stopping posts a Jira worklog.
- Buttons are ordered by priority (1 at the top). Hovering shows the ticket title. A button can also open the ticket in the browser.
- Right-clicking a button opens a comment editor with Jira markup, images, tables, and internal or public visibility.
- The bar floats anywhere or docks to any screen edge like the Windows taskbar.
- Sign-in works like Jira Assistant: Jira Cloud with email and API token.

## Decisions

| Topic | Choice |
|---|---|
| Jira | Cloud, Basic auth with email + API token (classic or scoped) |
| Ticket source | User-configurable JQL |
| Time logging | Worklog posted when a timer stops |
| Stack | .NET 10, WPF, WPF-UI 4.3, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf, WebView2, xUnit |
| Secrets | DPAPI (CurrentUser) in `%LOCALAPPDATA%\JiraTaskBar\credentials.bin` |

## Layout

```
JiraTaskBar.slnx
src/JiraTaskBar.Core/         Jira client, models, payloads, tracking, storage, wiki preview
src/JiraTaskBar.App/          WPF app: bar, comment, settings windows, AppBar interop, tray
tests/JiraTaskBar.Core.Tests/ 87 tests
tests/JiraTaskBar.App.Tests/  29 tests (window smoke tests, shell, type glyphs)
build/publish.ps1             release build -> artifacts\publish\JiraTaskBar.exe
docs/THREAT_MODEL.md          STRIDE threat model and action table
Directory.Build.props, nuget.config, packages.lock.json (supply-chain hardening)
```

## Work done, in order

1. **Initial build:** the full app as planned.
   - Ticket bar sorted by priority, with tooltips and open in browser.
   - Single-active timer with crash recovery and idle/sleep handling.
   - Worklog retry queue.
   - Comment editor with preview, tables, pasted or attached images, and internal/public visibility.
   - AppBar docking on any edge and monitor (per-monitor DPI), drag-to-snap.
   - Tray icon, start with Windows, single instance.
   - Git repo initialised with an initial commit.
2. **Threat model** (`docs/THREAT_MODEL.md`). It found three defects, all fixed (`fix/threat-model-defects`):
   - comments could be posted as public by default
   - an `http://` site URL was accepted, which would send the token in clear text
   - unsafe URL schemes reached ShellExecute
3. **Hardening, actions 4, 5, 7, 9, 10** (`fix/threat-model-hardening`):
   - Credentials (site, email, token) are stored together, with migration from the old `token.bin`.
   - HTTPS-only `*.atlassian.net` and no auto-redirects.
   - Preview CSP, with remote images blocked.
   - 429/503 retries honouring Retry-After.
   - A 401 triggers a re-auth prompt and pauses refresh.
   - Temp and WebView2 folders are cleaned up.
   - Lock files, NuGet audit, package source mapping, and signature validation.
4. **Next fixes** (`feat/scoped-tokens-capture-privacy`):
   - Scoped API tokens through the `api.atlassian.com/ex/jira/{cloudId}` gateway.
   - Optional exclusion from screen capture (`WDA_EXCLUDEFROMCAPTURE`).
   - Option to show keys only on the bar.
   - Per-window cleanup of the preview folders.
5. **Publish fix** (`fix/publish-lock-files`): publishing failed with NU1004 because the win-x64 RID and ILLink packages were missing from the lock files. The fix adds `RuntimeIdentifiers` and `EnableSingleFileAnalyzer` to `Directory.Build.props` and regenerates the lock files.
6. **Work-type icons** (`feat/issue-type-icons`):
   - Each button and tooltip shows the Jira issue-type avatar, fetched as PNG, size-capped, signature-checked, and cached.
   - If the avatar can't be loaded, a Fluent glyph chosen from the type name is shown instead.
7. **Cancel crash fix** (`fix/close-while-closing`): "Cannot set Visibility … while a Window is closing" appeared when cancelling an unsent comment.
   - Fix: cancel the close and defer the discard prompt with `Dispatcher.BeginInvoke`.
   - The same fix applies to Alt+F4 on the bar.
   - A regression test was added.

All branches were fast-forward merged into `main` and are kept. There is no remote, and nothing has been pushed. `main` is at `99a4955`.

## Useful answers from the session

- **Release exe:** `artifacts\publish\JiraTaskBar.exe` (self-contained single file, about 65 MB, with a `.sha256` next to it). Rebuild it with:
  `powershell -ExecutionPolicy Bypass -File build/publish.ps1` (optional `-CertificateThumbprint` to sign).
  The current exe predates the work-type icons and the Cancel fix, so rebuild it.
- **JQL for my tickets in one project:**
  `project = ABC AND assignee = currentUser() AND statusCategory != Done ORDER BY priority DESC, key ASC`

## Open items

From the threat model action table:
- **10 (IT part):** the company package feed policy, a code-signing certificate, and installing to Program Files. The code side is done.
- **11:** check attachment visibility on public comments, in a non-production Jira Service Management project.
- **12:** OAuth 2.0 (3LO) instead of API tokens.

Other:
- Not verified: the issue-type avatar endpoint with `format=png` on scoped tokens. If it fails, the glyph fallback covers it.
