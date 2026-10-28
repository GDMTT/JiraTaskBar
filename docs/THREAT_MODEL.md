# Jira TaskBar – Threat Model

| | |
|---|---|
| **Scope** | Jira TaskBar desktop app (commit `b2bd812`), how it authenticates to Jira Cloud, its local data, and its build supply chain |
| **Method** | Data-flow diagram + STRIDE per trust boundary, rated by likelihood × impact |
| **Date** | 2026-10-28 |
| **Out of scope** | Jira Cloud's own security, the Windows host's hardening, Atlassian account and IdP configuration (these are covered only where the app depends on them) |

## 1. Summary

The attack surface is small. The app has no listening ports, no IPC beyond a single-instance mutex, no elevation, no auto-update, and it talks only to the Jira site the user configures, over HTTPS.

The biggest risk comes from the **authentication method itself**. An Atlassian API token is equivalent to a password, lives for a long time, and **bypasses SSO and MFA**. A classic (unscoped) token can do anything the user can do in Atlassian Cloud. Anyone who gets it, from the disk, memory, or a misdirected request, can act as the user from anywhere until it expires or is revoked.

The review found **three defects to fix before wider use**. All three are now **fixed** (actions 1–3 in §6):

1. **Comments can post as public by mistake (fail-open).** The internal/public choice is sent only when the app *knows* the ticket is a Service Management ticket. When it doesn't know (for example a ticket that dropped out of the query while its timer runs), no visibility flag is sent, and Jira Service Management treats the comment as **public: it is shown to the customer and emailed to them**.
2. **The site URL accepts plain `http://`**, which would send the token unencrypted. Any host is also accepted, so the token goes to whatever site URL is configured.
3. **Preview links opened with Ctrl-click or middle-click** go to `ShellExecute` without a scheme check, so `file://` and other protocol links can launch.

## 2. System overview

```
                       ┌──────────────── Windows user session (trust zone A) ───────────────┐
                       │                                                                     │
  User ── mouse/kbd ──►│  JiraTaskBar.exe (WPF, runs as user)                                │
  Clipboard/drag-drop ►│   ├─ Bar / Comment / Settings windows                               │
                       │   ├─ AppController, TimeTracker, WorklogService                     │
                       │   ├─ JiraClient (HttpClient, Basic auth)  ───────── TB3 ─────────┐  │
                       │   ├─ WebView2 preview (separate msedgewebview2 processes)  TB4   │  │
                       │   └─ ShellExecute → default browser / Explorer             TB5   │  │
                       │                                                                  │  │
                       │  %LOCALAPPDATA%\JiraTaskBar   (TB2: any same-user process)       │  │
                       │   settings.json · credentials.bin (DPAPI) · state.json ·         │  │
                       │   pending-worklogs.json · log.txt · pending\<guid>\* · WebView2\ │  │
                       │  HKCU\...\Run\JiraTaskBar                                        │  │
                       └──────────────────────────────────────────────────────────────────┼──┘
                                                                                          │ HTTPS
                                                         ┌────────────────────────────────▼──┐
                                                         │ Jira Cloud  <site>.atlassian.net  │
                                                         │ /rest/api/3 search, worklog,      │
                                                         │ attachments · /rest/api/2 comment │
                                                         └───────────────────────────────────┘
  Build time (TB6): nuget.org → WPF-UI, H.NotifyIcon.Wpf, CommunityToolkit.Mvvm, WebView2, ProtectedData
```

**Trust boundaries**

| ID | Boundary | Notes |
|---|---|---|
| TB1 | User ↔ app | Same person. Input also comes from the clipboard and drag-drop, which may carry content from untrusted sources (emails, web pages). |
| TB2 | App ↔ local disk / registry | Any process running as the same user can read and write these files. |
| TB3 | App ↔ Jira Cloud | Internet, TLS. Carries the credential. |
| TB4 | App ↔ WebView2 | Renders markup the user wrote or pasted. Scripts are disabled. |
| TB5 | App ↔ Windows shell | `ShellExecute` of URLs and folders. |
| TB6 | Build ↔ package feeds | Third-party code that ships inside the exe. |

## 3. Assets

| Asset | Why it matters | Where |
|---|---|---|
| **A1 API token** | Equivalent to a password for the user's Atlassian account. Bypasses SSO/MFA. | `credentials.bin` (DPAPI, together with site and email), process memory, `Authorization` header |
| **A2 Comment confidentiality** | Internal notes on Service Management tickets must not reach customers. | Comment editor, Jira API |
| **A3 Worklog integrity** | Time records may feed billing, SLAs and payroll. | `state.json`, `pending-worklogs.json`, Jira |
| **A4 Attachments / screenshots** | Pasted screenshots often contain sensitive data. | `pending\<guid>\`, WebView2 cache, Jira |
| **A5 Ticket metadata** | Ticket titles can name customers, incidents or people. | Bar UI (always on top), tooltips, `log.txt` |
| **A6 Configuration** | Controls *where* the token is sent and which tickets are shown. | `settings.json` |

## 4. Authentication analysis

### 4.1 What the app does today
- It sends `Authorization: Basic base64(email:apiToken)` on every request to the configured site ([JiraClient.cs:33](../src/JiraTaskBar.Core/Jira/JiraClient.cs#L33)).
- The token is validated once with `GET /rest/api/3/myself`, then stored encrypted with DPAPI for the current user, using fixed app-specific entropy ([SecretStore.cs:11](../src/JiraTaskBar.Core/Storage/SecretStore.cs#L11)).
- TLS uses .NET defaults: certificate validation is on, and the OS trust store is used, so corporate TLS inspection keeps working.

### 4.2 Properties of the credential

| Property | API token (current) | Implication |
|---|---|---|
| Scope | A classic token has the user's **full** permissions across Atlassian Cloud (Jira and Confluence). | A leaked token exposes far more than time tracking. |
| Lifetime | Long-lived. Atlassian now requires an expiry on new tokens, up to one year. | Exposure lasts for months. Expiry also breaks the app silently, showing only a 401 status. |
| SSO / MFA | **Not enforced.** The token authenticates without going through the IdP. | Theft is not limited by device or MFA, unless the organisation uses Atlassian Guard IP allowlisting or token policies. |
| Revocation | Manual, by the user at id.atlassian.com, or by an org admin. | Fine, but someone has to notice the leak first. |
| Org policy | Atlassian org admins can block API tokens for managed accounts. | **Check Gallagher's Atlassian policy** before rolling this out. Blocking tokens may be intended. |

### 4.3 Storage (DPAPI, CurrentUser)
- **Protects against:** other Windows users on the machine, and offline theft of the disk or a backup, unless the attacker also has the user's password or the domain DPAPI backup key.
- **Does not protect against:** malware running as the user. It can call `CryptUnprotectData` itself, because the entropy is a constant in the binary. Windows Credential Manager gives the same protection level, so switching to it wouldn't change this. This limit is inherent to any desktop app that keeps a reusable secret.
- **Domain admins** can decrypt using the domain DPAPI backup key. This is normal in a corporate environment.

### 4.4 Comparison with alternatives

| Option | Security | Effort / notes |
|---|---|---|
| **Classic API token** (now) | Weakest: unscoped, long-lived, no MFA | Simplest. Same as Jira Assistant's token mode. |
| **Scoped API token** (recommended next step) | Limited to the scopes it is given, for example `read:jira-work`, `write:jira-work`, `read:jira-user`. A leak exposes only Jira work items, not Confluence or admin. | Small change. Scoped tokens are called through `https://api.atlassian.com/ex/jira/{cloudId}/…` rather than the site URL; the cloud ID comes from `https://<site>/_edge/tenant_info`. |
| **OAuth 2.0 (3LO)** | Best: short-lived access tokens, sign-in goes through SSO and MFA, granular scopes, admin-visible consent and revocation. | Atlassian's 3LO has historically required a client secret, which a desktop app can't keep. Unless PKCE for public clients is confirmed for your app, it needs a small token-broker service. That brings the most work and something to host. |
| **Browser session reuse** (how the Jira Assistant *extension* works) | Inherits SSO and MFA, with no stored secret | Not available to a desktop app without cookie scraping, which is fragile and a security smell. Not recommended. |

**Recommendation:** move to **scoped API tokens** now: limited blast radius, low effort. Treat **OAuth 3LO** as the target if the app is distributed beyond personal use or if the org restricts API tokens.

## 5. Threats (STRIDE)

Ratings: **H**igh / **M**edium / **L**ow for likelihood × impact. ✅ means the threat is already mitigated.

### Spoofing

| ID | Threat | Rating | Current state | Mitigation |
|---|---|---|---|---|
| S1 | **Token sent to the wrong host.** The site URL can be any host, including a typo, a look-alike domain, or a malicious edit to `settings.json`. The token is attached to every request. | **M** | `NormalizeSiteUrl` accepts any absolute URL ([Models.cs:28](../src/JiraTaskBar.Core/Jira/Models.cs#L28)). | Only accept `https://<name>.atlassian.net` (the deployment is Cloud-only). Store site and email *inside* the DPAPI blob together with the token, so editing `settings.json` can't redirect the credential. |
| S2 | **Plain-HTTP site URL** (`http://acme.atlassian.net`) sends Basic credentials in cleartext before Atlassian's HTTPS redirect. | **H** (impact) / L (likelihood) | The scheme is kept as typed ([Models.cs:34](../src/JiraTaskBar.Core/Jira/Models.cs#L34)). | Reject anything that isn't `https`. Also set `AllowAutoRedirect = false` and treat 3xx responses as errors, so credentials never follow a redirect. Current .NET strips `Authorization` on redirect, but make it explicit. |
| S3 | Man-in-the-middle on Jira traffic. | L | ✅ Default TLS validation, and there is no override callback. | Keep it that way. Never add `ServerCertificateCustomValidationCallback`. |
| S4 | A fake or modified JiraTaskBar.exe captures the token, for example by replacing the exe in a user-writable folder that the Run key points at. | L | The exe is unsigned. The Run key points to wherever it was launched from. | Sign the exe (Authenticode). Install it to `%ProgramFiles%`, which needs admin to write, and point the Run key there. |

### Tampering

| ID | Threat | Rating | Current state | Mitigation |
|---|---|---|---|---|
| T1 | Same-user malware edits `pending-worklogs.json` or `state.json` to inject false worklogs, which are then posted under the user's identity. | L | Files are plain JSON. The threat assumes the host is already compromised, and at that point the token itself is exposed anyway (see §4.3). | Accept. Optionally show pending items before the first retry after a restart. |
| T2 | Time records are wrong because of the clock, or because the user picks the wrong recovery or idle option. | L | ✅ Recovery and idle prompts default to the non-destructive choice. Discarding asks for confirmation. | Accept. |
| T3 | Malformed or hostile JSON from the server. | L | Parsing is defensive. Exceptions end up in the bar's status line, and the app doesn't crash. | Accept. |

### Repudiation

| ID | Threat | Rating | Current state | Mitigation |
|---|---|---|---|---|
| R1 | A user disputes a worklog or comment ("the app posted that"). | L | ✅ Jira records the author and time. `log.txt` records each worklog and comment with key, duration and outcome. | Accept. Add the Jira worklog/comment ID to the log line for traceability. |

### Information disclosure

| ID | Threat | Rating | Current state | Mitigation |
|---|---|---|---|---|
| **I1** | **An internal note is posted as a public customer reply because the ticket's project type is unknown.** A comment with no `sd.public.comment` property is **public** in Jira Service Management. | **H** | Fail-open. The property is sent only when `IsServiceDesk` is true ([CommentWindow.xaml.cs:467](../src/JiraTaskBar.App/Views/CommentWindow.xaml.cs#L467)). Placeholder tickets have an empty project type ([TicketViewModel.cs:32](../src/JiraTaskBar.App/ViewModels/TicketViewModel.cs#L32)). | **Always** send `sd.public.comment` (it's harmless on other project types), defaulting to `internal: true`. If the project type is unknown, fetch it or force Internal. |
| I2 | A human chooses "Reply to customer" by mistake. Ctrl+Enter posts immediately. | M | Defaults to Internal ✅, but there's no extra friction for public replies. | Show a coloured "visible to customer" banner when Public is selected, and ask for confirmation before posting a public reply. |
| I3 | Attachments uploaded for an *internal* note may still be visible to customers on the portal, or visible to anyone with project browse permission. | M | **Unverified.** Files are uploaded through the issue attachments API before the comment is posted. | Test on a non-production Service Management project how attachments referenced from an internal note appear on the customer portal. Document the result, and warn in the UI if needed. |
| I4 | **Ticket titles show on screen shares.** The bar is always on top and its buttons and tooltips show summaries. It only steps aside for full-screen apps. | M | Not mitigated. | Add an option to exclude the bar from screen capture (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`), and/or a "keys only" mode. |
| I5 | Pasted screenshots remain on disk after a crash (`pending\<guid>\`), and the preview images stay in the WebView2 cache. | M | They are only deleted when the window closes normally. | Clean up `pending\` on startup. Clear the WebView2 cache on close, or use a temporary profile for it. |
| I6 | Preview loads remote images (`!https://…!`) in pasted markup, leaking IP address and timing to that host (a tracking pixel). | L | Remote URLs are allowed ([WikiPreview.cs:250](../src/JiraTaskBar.Core/Markup/WikiPreview.cs#L250)). | Block non-local images in the preview: show them as placeholders and add a Content-Security-Policy `img-src https://attachments.jirataskbar.local`. |
| I7 | `log.txt` contains ticket keys and Jira error text. | L | No token, comment body or headers are logged ✅. | Accept. |
| I8 | The token in process memory, visible in a crash dump. | L | Stored as a managed string. | Accept. This is a limit of .NET, and the threat requires same-user or admin access. |

### Denial of service

| ID | Threat | Rating | Current state | Mitigation |
|---|---|---|---|---|
| D1 | Another same-user process holds the `Local\JiraTaskBar.SingleInstance` mutex, so the app refuses to start. | L | — | Accept. |
| D2 | Jira rate limits (HTTP 429) during refresh or bulk retries. | L | Refresh runs every 5 minutes or more and fetches at most 200 issues. | Honour `Retry-After` on 429, and back off on retries. |
| D3 | The token expires or is revoked, so time stops being logged without the user noticing. | M | Worklogs queue up (shown with ⚠) and the status shows 401. | Show a clear "re-authenticate" prompt on 401. If the token's expiry date is known, remind the user before it arrives. |

### Elevation of privilege / code execution

| ID | Threat | Rating | Current state | Mitigation |
|---|---|---|---|---|
| **E1** | **A link in the preview is opened with `ShellExecute` without checking its scheme.** Markup pasted from an untrusted source could contain `file://\\host\share\x.exe` or a dangerous protocol handler. Opening it in a new window (Ctrl-click or middle-click) launches it. | **M** | Normal clicks are filtered to http(s) ✅, but `NewWindowRequested` calls `Shell.Open(args.Uri)` with no check ([CommentWindow.xaml.cs:403-406](../src/JiraTaskBar.App/Views/CommentWindow.xaml.cs#L403)). | Allow only `https:`, `http:` and `mailto:` in `Shell.Open` itself. Also disable new windows in the preview. |
| E2 | Supply chain: a compromised NuGet package ships inside the exe. | M | 5 packages, 2 of them third-party (WPF-UI, H.NotifyIcon). The repo `nuget.config` points at nuget.org, **overriding a machine config that has no sources**, which may be a deliberate corporate control. | Ask IT whether an internal feed or proxy (Artifactory, Azure Artifacts) is mandated, and switch to it. Enable `RestorePackagesWithLockFile`, package source mapping, and NuGet audit (`NuGetAuditMode=all`). Check versions before upgrading. |
| E3 | DLL planting in the app folder, for example `WebView2Loader.dll`. | L | Requires write access to the app folder, as the same user. | A single-file publish to `%ProgramFiles%` with signing (see S4). |
| E4 | WebView2 renderer exploit through crafted markup. | L | ✅ Scripts disabled, dev tools disabled, HTML-encoded output, navigation restricted to the virtual host, and the renderer runs in its own sandboxed process. | Keep the WebView2 runtime updated. It updates itself through Edge. |

## 6. Prioritised actions

| # | Action | Fixes | Effort | Status |
|---|---|---|---|---|
| 1 | Always send `sd.public.comment`, defaulting to internal. Show the internal/public choice when the project type is unknown (defaulting to Internal). | I1 | XS | ✅ Done |
| 2 | HTTPS-only site URL limited to `<name>.atlassian.net` (no port, no credentials in the URL); the client refuses non-HTTPS; redirects are not followed and are reported as errors; an invalid saved site reopens Settings instead of being used | S1, S2 | S | ✅ Done |
| 3 | `Shell.OpenUrl` allows only http/https/mailto (no UNC); preview navigation and new-window requests both go through it; folders open through a separate `OpenFolder` | E1 | XS | ✅ Done |
| 4 | "Reply to customer" shows a warning banner and relabels the button "Send to customer"; posting (including Ctrl+Enter) asks for confirmation, mentioning any attachments | I2 | S | ✅ Done |
| 5 | Startup deletes leftover `pending\` screenshots and the WebView2 cache folder; the preview never fetches remote images (placeholder instead) and has a CSP: `default-src 'none'`, images only from the local attachments host | I5, I6 | S | ✅ Done |
| 6 | Switch to **scoped API tokens** and document the minimum scopes | §4, S1 | M | Open |
| 7 | Site, email and token are stored as one DPAPI blob (`credentials.bin`, new entropy); `settings.json` no longer holds them. Changing the site needs the token re-entered. A v1 `token.bin` is migrated once, using the v1 `settings.json` site, then deleted | S1 | S | ✅ Done |
| 8 | Screen-capture exclusion / keys-only option | I4 | S | Open |
| 9 | 401 shows one "Jira sign-in needed" prompt that leads to Settings; automatic refreshes and retries pause until the token is replaced (repeated failed sign-ins can trip Atlassian's account protection); worklogs stay queued. 429/503 are retried up to 3× honouring `Retry-After` (capped at 60 s), else 2/4/8 s back-off | D2, D3 | S | ✅ Done |
| 10 | Code side done: `packages.lock.json` (locked mode in CI/release), NuGet Audit on all packages with high/critical failing the build, package source mapping, nuget.org repository signature **required** (trusted certs from `dotnet nuget trust source`), `build/publish.ps1` (locked restore → tests → single-file publish → optional Authenticode signing with timestamp → SHA-256). **Still needs IT:** confirm whether an internal feed is mandated, provide a code-signing certificate, and install under Program Files | E2, E3, S4 | M | ◐ Partly done |
| 11 | Verify attachment visibility on Service Management | I3 | XS (test) | Open |
| 12 | Evaluate OAuth 3LO (token broker) if the app is distributed beyond personal use | §4 | L | Open |

**Residual notes on the actions above**
- **S1:** settings tampering can no longer redirect the token. The one-time v1 migration trusts the v1 `settings.json` site, and that site is still checked against `*.atlassian.net`.
- **I5:** the WebView2 cache is cleared at the *next start*, not when the comment window closes, so cached preview images can stay on disk while the app runs.
- **D3:** the token's expiry date isn't available through the API, so there's no advance warning; the app reacts at the first 401.

## 7. Assumptions and residual risk

- The Windows host is managed and patched. Malware running as the user is out of scope; once it exists, any locally stored reusable credential is exposed (§4.3).
- Only Jira Cloud is used. Data Center support would need its own analysis (PAT handling, internal CAs).
- After the actions above, the remaining risk is a stolen token used outside SSO/MFA until it expires. Scoped tokens with short expiry reduce this, and only OAuth largely removes it.
