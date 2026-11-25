# CloudOrc Remote Workspace — Implementation Plan (2 Phases)

Source: `CloudOrc_Remote_Workspace_Dev_Handoff_v1.2.docx` (21 Nov 2026). Sections 40–54 of that
document take precedence over earlier sections.
Branch: `varun-tsplus` in both `Server-Agent` and `Cloude_Website`.
Status: **Phase 1 committed (24 Nov 2026). Phase 2 built and live-tested on WIN-7FD5JOCI12B (25 Nov 2026), except
the native Credential Provider DLL (no C++ toolchain on the build server — see docs/SECURE_ACCESS_CREDENTIAL_PROVIDER.md).**

### Phase 2 completion notes (25 Nov 2026)
| Item | State | Notes |
|---|---|---|
| 2.1 Secure Access — backend decision, agent Secure-Access Broker pipe, panel Security card | Done, live-tested | Decision reuses secureAccess.evaluateSecureAccess + rdpMfa; NOT_MANAGED accounts never locked out; fail closed for managed seats |
| 2.1 Native Credential Provider shim (C++) | **Not built** | Needs VS Build Tools + Windows SDK and a test VM; contract and recovery path documented |
| 2.2 Remote Assistance with consent | Done, live-tested | Provider = Windows RDS shadow; consent popup (view default, control opt-in, elevation never), red Stop bar, lease expiry, audit; admin request/end/join command in Agent monitoring |
| 2.3 Users & Access (customer admin) | Done, live-tested | Reuses windowsUser owner-scoped service + OTP step-up; Add user opens the portal |
| 2.4 File transfer — "Send a file to my PC" | Done (email step not live-tested) | Existing rdpTransfer policy-checked agent download; single-use 15-min pickup link emailed to the seat |
| 2.5 Network readiness (P0) | Done, live-tested | network_zone data, checklist with stable reason codes, Failure Lens in Agent monitoring; **not yet wired to block the provisioning READY state** (needs provisioning owner sign-off) |
| 2.6 Optional extras (CloudOrc desktop, web shell, Local Companion) | Not started | Marked "only if approved" in this plan |

### Remaining-points pass (25 Nov 2026, after Phase 2 commit)
| Point | State | Notes |
|---|---|---|
| Login 2FA gate | Improved, live-tested | Existing OTP gate (CloudOrcOtpGate + watcher) kept. New agent `SessionFeedReporter` pushes the server's sessions on every change + 20 s heartbeat to `POST /remote-workspace/agent/sessions`; the watcher runs immediately for that server and uses the feed instead of remote PowerShell. Stale feed (old agent) -> old 5 s poll, so nothing regresses. Native Credential Provider still not built (see above). |
| Readiness gates provisioning READY | Done behind flag, live-tested | `verifySubscriptionProvisioningDeliverables` calls `readinessGateForCustomer` after the existing checks. Off unless `REMOTE_ACCESS_READINESS_GATE=1`. BLOCKED -> `remote_access_<code>`; DEGRADED and internal errors never block; agents too old to report endpoint evidence are SKIPPED, not failed. |
| File-send email | Partly verified | Denied path (403) and reused link (410) verified; full download + email not run (test customer has no workspace folder; no real email sent on purpose). |
| Partner Remote Assistance | Done, live-tested (backend) | Partner portal: user detail page -> "Remote assistance" card. Routes `GET/POST /partner-workspace/customers/:id/remote-console/users/:wid/assistance`, `POST …/assistance/:rid/cancel`; ownership via the seat view (404 for other partners' seats), every read/cancel pinned to that seat's server + username; consent prompt shows the partner's brand name. Team members need `servers.console.console` to request. |
| On-server checklist run (25 Nov) | See notes | Support 20/20 (view-only grant, control needs new consent, lease + request expiry, decline, user Stop, other session/server 404, audit). Auth 26/26 (admin/panel/agent/partner routes refuse no/forged token). Network 15/15 (unroutable IP -> NETWORK_ROUTE_UNREACHABLE in ~3 s, TSPlus PIDs unchanged; stopped/listener/HTTP/stale evidence codes; firewall WARN only). MFA decision 9/9 + live broker: backend down -> managed seat fail-closed, Windows' own account NOT_MANAGED_OFFLINE, recovers when back. Logs: no password/OTP/token. 10+1 Notepads grouped, each listed, minimized one restored by Focus. Partner brand from backend OK. **Fixed while testing:** readiness used agent_endpoints.last_seen_at (only set at HELLO) -> now uses getAgentPresence; WebP partner logo cannot render in WPF -> PNG/JPG/favicon fallback. **Gaps:** engineer role revoked mid-session does not end an active assistance (lease/user Stop do); no automatic bounded TSPlus restart (readiness only advises); no WebSocket-specific check (REMOTE_WEBSOCKET_FAILED). Not testable here: 2 monitors/200%, two RDP users, 2FA-on RDP login, panel brand on screen (RDP window minimised), file email. |
| Seamless / RemoteApp panel (25 Nov) | Done, live-tested | Panel detects a RemoteApp session (rdpinit/rdpshell in the session = TSPlus .connect Remote App, RemoteApp .rdp, HTML5 RemoteApp). There: "YOUR APPS" launcher (published apps + folders, Open/focus), "Running now" without session plumbing (rdpinit, rdpshell, svcr, FloatingPanel), no "Switch to desktop", no "Hide for session", Alt+F4 only collapses; TSPlus panel replaced automatically once the panel has apps — TSPlus RemoteApp panel is svcr.exe "Remote App" (desktop one FloatingPanel.exe); in RemoteApp it is shrunk to 1x1 + empty region + transparent (transparency alone shows on the client), restored on exit/crash, orphans recovered at start, Watch runs `--recover-tsplus` once on a crash loop. Full desktop unchanged (Running now only, admin `hideTsplusPanel` as before). Verified on this server with a temporary RemoteApp user: TSPlus panel 288x306 -> 1x1, panel Open launched Notepad as a seamless window on the client (same Alt+Tab as local apps), full desktop session unchanged. HTML5 web session not run live (same session types). |
| Admin page in a browser | Not done by me | Needs an admin login; minting a token was (rightly) refused. Admin dev server: `npm run dev` in `admin/`, then Servers -> workspace -> Agent monitoring. |


### Phase 1 completion notes (24 Nov 2026)
- All Phase 1 items below are implemented: backend `remoteWorkspace` module + migration 0425, agent
  workspace pipe / token broker / health reporter / typed commands (`workspace.ui.restart`,
  `workspace.policy.refresh`), `CloudOrc.WorkspaceUI` (WPF), Watch supervision (restart, crash-loop
  breaker, last-known-good, disabled re-check, self-restart awareness), installer component, admin
  setting + panel health inside **Server workspace → Agent monitoring** (no new menu).
- Changes vs. the plan:
  - App launch runs in the user's session from the panel (the list is server-authorized; the launch
    is audited) — no `workspace.app.launch` agent command was needed.
  - TSPlus panel hiding is done by the CloudOrc panel making the TSPlus FloatingPanel window
    transparent + click-through in its own session (not SW_HIDE: a hidden modal window can be closed
    by its owner). TSPlus configuration is never changed; windows are restored on policy off, exit,
    crash (next start recovers orphans). **Verify against a real TSPlus FloatingPanel before a pilot.**
  - Ticket topic uses the central ticket category catalog (`GET /remote-workspace/support/categories`).
  - `CloudOrc.WorkspaceUI.exe --open=<tab>` opens the running panel (shortcut / accessibility / QA).
- Live test results and the remaining checks are listed in the session report; production-path
  checks still open: Watch as LocalSystem (CreateProcessAsUser path), a real TSPlus FloatingPanel,
  and the admin page in a browser with an admin login.

---

## 0. What we are building, in one paragraph

When a user logs in to their VPS over RDP or the browser, today they see the TSPlus floating
panel. We will replace it with a **CloudOrc floating panel**: a thin auto-hide rail on the right
edge with Apps, Backup, Files, Clipboard, Print, AI, Support, Alerts and More. TSPlus and RDP
stay underneath. The rail is a per-user Windows exe (`WorkspaceUI.exe`) that lives inside the
existing Server-Agent solution. Everything it shows comes from the existing Node backend.

Phase 1 is the panel itself. Phase 2 adds MFA at Windows login, consent-based remote support,
customer-admin tools, and the network readiness checks.

---

## 1. What already exists (reuse, do not rebuild)

Found by inspecting the code on 24 Nov 2026.

### Server-Agent (.NET 10, `net10.0-windows`)
| Existing | Reuse for |
|---|---|
| `CloudOrc.ControlAgent` (LocalSystem service, WSS to backend, enrollment, typed commands via `TypedCommandDispatcher`, durable inbox/outbox) | The "Agent Service" in the doc. New `workspace.*` typed commands and a new local pipe go here. |
| `CloudOrc.WatchdogAgent` (Named-pipe health, consecutive-failure tracker, rate limiter + backoff) | The "CloudOrc Watch" in the doc. Extend it to watch WorkspaceUI per user session, and add the crash-loop breaker. |
| `CloudOrc.Agent.Tray` (WinForms, started per user via HKLM `Run` key, talks to the agent over pipe `CloudOrc.Agent.Status`) | The proven pattern for per-user UI. WorkspaceUI starts the same way. |
| `AgentStatusPipeServer` (pipe ACL: SYSTEM, Admins, Authenticated Users) | The template for the new workspace pipe. |
| `CloudOrc.Agent.Updater` (signed update, directory backup) | Last-known-good rollback of WorkspaceUI. |
| `RdpTransferPolicy/*` | Real clipboard / drive-redirection state for the Clipboard and Files tiles. |
| Installer `CloudOrcAgentSetup.iss` (components, Run key) | Add a `workspace` component. |

### Backend (`Cloude_Website/backend/src/modules`)
| Existing module | Reuse for |
|---|---|
| `tsplusAccess` (`published_apps`, `customer_published_apps`, `customer_workspace_access`) | Published Apps list. No second app catalog. |
| `remoteAccess` (`remote_access_endpoints`, `remote_access_policies`, `remote_sessions`, `remote_access_launch_grants`, TSPlus proxy) | Session record, launch modes, clipboard/file/print policy, web remote. |
| `server/agentGatewayBus`, `agentCommandDurability` | Sending typed commands to the agent. |
| `backupV2` (commands, verified points) | "Run Backup Now" and the last verified backup. |
| `notifications` (+ socket) | Alerts tab. |
| `ticket`, `ai` | Support tab and AI tab. |
| `rdpMfa` (OTP challenge, `sessionGate`, `watcher`, remember policy) | Phase 2 MFA. The Credential Provider will consume this decision, not a new one. |
| `secureAccess` (IP restriction, `evaluate.service`) | Phase 2 secure-access decision. |
| `windowsUser` (create, unlock, reset, action OTP, 2FA, sessions) | Phase 2 Users & Access. |
| `supportAccess` (portal "act as customer" sessions) | Phase 2 remote assistance reuses its reason/TTL/audit model. It is not screen sharing today. |
| `tsplusBranding` | Hiding the TSPlus floating panel at the end of Phase 1. |

---

## 2. Key design decisions (please confirm)

| # | Decision | Recommendation | Why |
|---|---|---|---|
| D1 | UI framework for `WorkspaceUI.exe` | **WPF on .NET 10** | The doc says ".NET UI process" but names no framework. WPF gives smooth slide/auto-hide, high-DPI and modern styling. It is the same .NET 10 runtime, so Contracts code is shared. |
| D2 | How WorkspaceUI starts | **HKLM `Run` key** (same as Tray), plus Watch relaunches it into the user session (`WTSQueryUserToken` + `CreateProcessAsUser`) after a crash | Same proven pattern as Tray. The service never draws UI (doc §47). |
| D3 | How the panel talks to the backend | Panel → **pipe** → ControlAgent gets a **short-lived workspace token** from the backend for that Windows user and session → panel calls backend REST with that token | The agent can prove which Windows user is on the pipe (client SID and session id). The backend stays the authority. No password is stored on the VPS. Privileged actions always go backend → agent typed command. The panel never runs them locally. |
| D4 | Tray vs WorkspaceUI | **Keep Tray as it is in Phase 1.** WorkspaceUI shows the same agent status inside More. Retire Tray from the RDS image after the pilot. | Tray is still needed on headless and backup-only servers. |
| D5 | Credential Provider language (Phase 2) | **C++ COM DLL**, minimal. All logic lives in the .NET broker. | The doc says no .NET runtime inside LogonUI. |
| D6 | Remote assistance provider (Phase 2) | **RDS shadow** (`mstsc /shadow` with consent) behind `IRemoteAssistanceProvider`. Use TSPlus shadow if licensing allows. | The doc says to build no new screen or codec stack. |
| D7 | Feature flags | `remote_workspace_minimal_panel`, `workspace_admin_tools`, `secure_access_broker`, `native_credential_provider`, `watch_crashloop_guard`, `remote_assistance` | Rollout per server or organization. Flags are not authorization. |

---

## 3. PHASE 1 — Floating Panel (Minimal Panel experience)

**Goal:** after login the user sees the CloudOrc rail instead of the TSPlus panel. The rail is
fully functional for everyday work and survives its own crashes without touching Tally.

### 1.0 Contract freeze (backend + agent, no UI)
- Launch-mode enum: `DIRECT_APP`, `MINIMAL_PANEL` (default), `WINDOWS_DESKTOP`, `CLOUDORC_DESKTOP`, `WEB_REMOTE`. Map the existing `remote_access_policies.allowed_launch_modes` (`APP`, `FULL_DESKTOP`) onto it.
- Pipe protocol `CloudOrc.Workspace` (JSON, versioned): `hello`, `getContext`, `getToken`, `windowEvent`, `panelHeartbeat`.
- Reason codes for the panel: `WORKSPACE_UI_DEGRADED`, `AGENT_OFFLINE`, `POLICY_BLOCKED`, and so on.
- Events: `WorkspaceUiStarted/Stopped/Crashed/Recovered`, `AppWindowStateChanged`, `BackupRunRequested`.

### 1.1 Backend (Node, `Cloude_Website/backend`)
New module **`remoteWorkspace`**. It composes existing services and duplicates none of them.
| Item | Details |
|---|---|
| Migration | `remote_workspace_profile` (org/brand, default mode, allowed modes, panel policy, support route, aiEnabled, notification policy, version), `user_workspace_preference` (panelPinned, side, autoHideDelay), `runtime_component_health` (endpoint, component, version, status, restartCount, lastCrashRef). |
| `POST /remote-workspace/agent/token` | Agent-authenticated. Input: Windows username and session id. Output: short-lived JWT bound to (server, windowsUserId, session, audience `workspace-ui`). |
| `GET /remote-workspace/context` | Brand (white-label), user, org, resolved launch mode, entitled capabilities, `permittedActions`, support destination, policy version. |
| `GET /remote-workspace/apps` | From `tsplusAccess` published apps for this Windows user: id, name, icon, exe path, max instances. |
| `POST /remote-workspace/apps/:id/launch` | Uses the existing launch path. Idempotency key. |
| `GET /remote-workspace/capabilities` | Effective clipboard, file transfer and print state from `remoteAccess` and `rdpTransfer` policies. |
| `GET /remote-workspace/backup` + `POST .../backup/run` | Proxies `backupV2`: last *verified* point, history, run now (permission checked). |
| `GET /remote-workspace/notifications` | From `notifications`, filtered and deduplicated. |
| `POST /remote-workspace/support/ticket`, `POST /remote-workspace/ai/ask` | Thin adapters over `ticket` and `ai`. |
| `PATCH /remote-workspace/preferences` | Pin, side, collapse delay. |
| `POST /remote-workspace/health` | WorkspaceUI and Watch health and crash reports. |
| Portal | Admin and partner: launch-mode and panel-policy setting inside the **existing** remote-access/TSPlus settings page (no new menu). |

### 1.2 Agent (`CloudOrc.ControlAgent`)
- `WorkspacePipeServer`: new pipe `CloudOrc.Workspace`. It identifies the caller (`GetNamedPipeClientProcessId` → session id + user SID) and rejects mismatches. Per-session context only.
- `WorkspaceTokenBroker`: fetches and caches the per-user token from the backend, and refreshes it before expiry.
- Typed commands: `workspace.app.launch`, `workspace.ui.restart`, `workspace.policy.refresh`.
- `HELLO`/capabilities advertise `workspace-ui` so the backend knows the server supports it.

### 1.3 New project `CloudOrc.WorkspaceUI` (WPF, per user)
| Area | Behaviour (doc §27, §41, RW-01, RW-02, RW-07, RW-09) |
|---|---|
| Shell | 4 states: hidden edge handle → icon rail → expanded panel → pinned. Default right edge. Respects taskbar and work area, multi-monitor, left/right preference. |
| Auto-hide | Collapses after inactivity unless pinned, a modal is open, a critical alert is showing, or it has keyboard focus. Opens only on user gesture, hotkey or critical notification. Never stays open just because the session is connected. |
| Apps | Published apps from the backend. **Running now**: enumerate top-level windows in this WTS session, map exe → publishedAppId, group by app with a count badge (e.g. Tally ×3). Focus/Restore, Minimize, Close (WM_CLOSE only), Open New (respects max instances). No Alt+Tab hook. |
| App switcher (RW-08) | Opened from Apps or a non-conflicting hotkey. Icon + title + state; thumbnails via DWM if cheap. |
| Backup | Last verified point and age, stale/failed state, Run Now (if permitted), short history, link to the portal for settings. |
| Files / Clipboard / Print | Show the *effective* state from server policy. No fake toggles. File transfer in Phase 1 = status + guidance on the existing TSPlus/RDP drive transfer. A chunked upload/download panel is Phase 2. |
| Support | Ticket form with automatic context (session id, app, health summary). Routed to Partner or CloudOrc per brand. |
| AI | Chat pane using backend `ai`. Read and guide only. No local system changes. |
| Alerts | Feed with severity. Toast only for critical alerts. Never steals focus. |
| More | Display, Session info, agent status (from Tray data), Settings, Sign out (separate from disconnect, with a warning). |
| Branding | Logo, name and colours from `/context`. Nothing hardcoded as CloudOrc. |
| Accessibility | Keyboard reachable, focus visible, UIA names, high contrast, 200% scaling. |
| Resilience | Panel data loads in parallel with timeouts. If the backend or agent is down, the panel shows a degraded state and the apps keep working. |

### 1.4 Watch (`CloudOrc.WatchdogAgent`)
- Monitors WorkspaceUI per active WTS session (process + pipe heartbeat).
- On crash: bounded restart with backoff into that user session (D2).
- **Crash-loop breaker:** after N crashes in M minutes, stop restarting, report `WORKSPACE_UI_DEGRADED` with evidence, and roll back to the last-known-good UI build if the crash correlates with an update.
- Never restarts TSPlus or the agent because of a UI problem.

### 1.5 Installer & rollout
- New installer component `workspace` (WPF binaries + Run key `CloudOrcWorkspaceUI`), off by default and enabled by flag or command.
- Hide the TSPlus floating panel for pilot servers only (through `tsplusBranding`), once the CloudOrc panel is healthy.

### 1.6 Phase 1 test checklist (doc §51)
- Native Windows: Alt+Tab, Alt+Shift+Tab, taskbar, minimize/restore with 1, 3 and 10 windows, multiple Tally instances, direct-app mode.
- Panel never covers the taskbar; works with 2 monitors and 200% scaling.
- Kill WorkspaceUI: Tally stays open, panel comes back quietly. Crash it 5 times: breaker stops, desktop still usable.
- Agent offline: agent-dependent actions are disabled with the last-known time; apps keep working.
- Two users on the same server: no cross-session windows or data.
- Brand switch (partner vs CloudOrc) shows the correct logo and support route.
- Backup shows verified state, not "job accepted".

**Phase 1 exit:** pilot servers run the CloudOrc panel with the TSPlus panel hidden, and all of 1.6 passes.

---

## 4. PHASE 2 — Security, Support, Admin & Reliability depth

### 2.1 MFA at Windows login (doc §29–30, RW-03)
- **Inspect `rdpMfa` first:** how `sessionGate` and `watcher` enforce today. Keep its OTP, remember policy and verification as the decision source.
- **`SecureAccessBroker`** (.NET, inside ControlAgent or a sidecar service): local pipe with a tight ACL (LocalSystem only, for LogonUI). Signed cached policy, calls `secureAccess.evaluate` + `rdpMfa`. Returns `{allow, reasonCode, policyVersion, expiresAt, correlationId}`. Fail closed for unknown device, privileged access or expired policy.
- **`CloudOrc.CredentialProvider`** (C++ COM DLL): wraps the Windows password tile and adds the OTP/approval step. Windows still checks the password. No Winlogon patching. Install and uninstall must be safe, with a documented recovery path (safe-mode removal) so a bad DLL cannot lock everyone out.
- Web remote: portal MFA → existing `remote_access_launch_grants` (short-lived, audience-bound).
- Panel Security view: trusted device, MFA verified, session expiry, and step-up for sensitive actions.
- Break-glass: time-bound, reasoned and audited admin path.

### 2.2 Remote Assistance with consent (doc §46, RW-04, RW-05)
- Backend: `remote_assistance_request` (who, org, ticket/reason, target session, requested capabilities, TTL, consent decision, start/end, provider ref, outcome) + API (create, grant/deny, start, change capability, stop, verifyEnded). Reuses the `supportAccess` RBAC/TTL/audit model.
- Agent: `SupportWorker` (on-demand process) implementing `IRemoteAssistanceProvider` with RDS shadow (D6).
- Panel: consent popup with engineer, organization, reason and duration. View only is pre-selected. Control, Clipboard and File need explicit ticks. Elevation is separate and never pre-ticked. A persistent banner with **Stop** stays visible while the session is active. Stop revokes immediately.
- Admin/partner support console: request button on the customer's session.

### 2.3 Users & Access for customer admins (doc §28)
- Panel → More → Users & Access (only if `permittedActions` allows it): create user, unlock, reset password, app access, active sessions, disconnect.
- Everything goes through the existing `windowsUser` service and its action-OTP / step-up. No new provisioning logic.

### 2.4 File transfer panel (doc §32)
- Chunked upload/download with progress, cancel, resume, size/type policy (reuses the clipboard extension whitelist rules) and audit.

### 2.5 Network readiness — doc Part A, "P0" only
- Tables `data_center`, `network_zone`, `connectivity_profile`, `connectivity_probe`, `provisioning_check`. Register zones 10.47.144.0/24 and 10.47.145.0/24.
- Provisioning READY only after the mandatory checks pass (route, return path, firewall, agent heartbeat, TSPlus service/listener, local HTTP, proxy→VPS TCP/HTTP/WebSocket, E2E).
- The 14 stable reason codes. Agent health payload extended with network, firewall and TSPlus sections.
- Remove any hardcoded `10.47.*` branching found in business code.
- Server detail page gets a Failure Lens (expected vs observed, reason, safe action) inside the **existing** server page.
- Not in scope: multi-DC, private WAN, agent tunnel (doc P2–P4).

### 2.6 Optional extras (only if approved)
- `CLOUDORC_DESKTOP` branded virtual desktop mode.
- Web remote CloudOrc shell around the TSPlus HTML5 iframe (React), with a mobile bottom dock.
- `CloudOrc.LocalCompanion` on the user's own PC for one-click Local/Cloud switching.

### 2.7 Phase 2 test checklist
- MFA: known/new device, allowed/blocked IP, OTP success/fail/timeout, offline grace, privileged fail-closed, break-glass audit, native RDP and web. No OTP or password in logs.
- Support: View only, expand to Control (needs new consent), deny, TTL expiry, user Stop, engineer role revoked mid-session.
- Admin: each action with role denied, step-up, stale target, partial failure.
- Network: remove route → `NETWORK_ROUTE_UNREACHABLE` and TSPlus is *not* restarted; stop TSPlus → bounded restart; HTTP OK but WebSocket fails → `REMOTE_WEBSOCKET_FAILED`.

---

## 5. Not doing (doc non-goals)
No second Windows agent, no second app catalog, no second backup engine, no Alt+Tab hook,
no Winlogon patching, no own screen-share codec, no public-IP fallback, no broad firewall
rules, no tunnel/multi-DC build, no new duplicate portal menus.

## 6. Working method (doc §52)
For each slice: Inspect → Plan → Implement → Test → Compare → Fix → Verify. Builds, tests and
installer/APK packaging run only when asked.

## 7. Open questions for the owner
1. D1–D7 above: OK?
2. Does network P0 (§2.5) stay in Phase 2, or should it run in parallel with Phase 1 because the doc calls it "required now"?
3. Is the doc's formal document ID (CO-DEV-INFRA-003 conflict) needed before we start, or is it only paperwork?
4. Which pilot server(s) and organization should get the panel first?
