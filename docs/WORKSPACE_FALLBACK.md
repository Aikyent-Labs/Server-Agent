# Remote Workspace fallback — default Windows / TSPlus experience

Goal: whatever fails, a customer is never stuck. The CloudOrc panel can always be taken out of the way:
TSPlus floating panel visible again, Windows Alt+Tab, normal desktop.

## Layers

| Layer | Who | What | Where |
|---|---|---|---|
| Panel self-protection | automatic | Alt+Tab hook on its own thread; if the UI does not answer in 1.5 s, Alt+Tab goes to Windows and the hook is switched off for the session. Any crash restores the TSPlus panel. | `CloudOrc.WorkspaceUI/Services/AltTabHook.cs`, `App.xaml.cs` |
| Watch | automatic | Crash → restart with backoff. 3 crashes / 10 min → stop (DEGRADED) + TSPlus panel restored. No heartbeat for 90 s → end the hung panel, restore TSPlus, restart. Switch on → never start; end a panel that ignores it after 30 s. | `CloudOrc.WatchdogAgent/Workspace/WorkspaceUiRecoveryPolicy.cs`, `WorkspaceUiSupervisor.cs` |
| Control Agent | automatic | Syncs the team switches to `fallback-team.json` (every minute + on `workspace.fallback.sync`; keeps the last value offline). Starts a stopped Watch service (installed service only, 3×/hour) and reports it as the `WATCHDOG` component. | `CloudOrc.ControlAgent/Workspace/WorkspaceFallbackSync.cs`, `WatchdogGuard.cs` |
| CloudOrc team | admin panel | Kill switch: all servers / a partner / a server / one user × whole panel / Alt+Tab only / TSPlus hiding only. Reason required, optional expiry, audit, alerts. Permission `SERVER.WORKSPACE.FALLBACK`. | Admin → server workspace → Agent monitoring → "Workspace safety & fallback" |
| Server administrator | on the server | Local switch when CloudOrc cannot reach the server. | `CloudOrc.ControlAgent.exe workspace fallback ...` |

Switch files: `%ProgramData%\CloudOrc\Workspace\fallback-team.json` and `fallback-local.json`. They are trusted only when
owned by SYSTEM or Administrators (a Windows user cannot switch their own panel off). Panel polls every 5 s, Watch every 10 s.

## Alerts (admin notifications, one per server + kind per 6 h)

Crash loop, hung panel, local switch used, Watch stopped / not reporting, agent silent > 15 min on a server with
workspace users, every team switch on/off. A panel that crash-loops or hangs after an agent update counts as a failed
update in the staged rollout, so a bad release halts itself.

## Runbook

1. **Customers report the panel misbehaving on one server** → Admin → server → Agent monitoring → Workspace safety →
   "This server" + "Whole CloudOrc panel off" + reason (ticket). Applied in ~3 s on an online agent.
2. **Bad release on many servers** → "ALL servers", type `ALL SERVERS`; pause the agent rollout.
3. **Agent silent / switch cannot reach the server and customers are stuck** → RDP as administrator:
   ```
   "C:\Program Files\CloudOrc\Agents\ControlAgent\CloudOrc.ControlAgent.exe" workspace fallback on --reason "TKT-..."
   "...\CloudOrc.ControlAgent.exe" workspace fallback on --user NAME --reason "..."      (one user)
   "...\CloudOrc.ControlAgent.exe" workspace fallback on --alt-tab-only --reason "..."   (only Alt+Tab)
   "...\CloudOrc.ControlAgent.exe" workspace fallback status [--user NAME]
   "...\CloudOrc.ControlAgent.exe" workspace fallback off [--user NAME | --all]
   ```
4. **Watch stopped** → `Start-Service CloudOrcWatchdogAgent` (the Control Agent also tries 3× an hour).
5. When fixed: turn the switch off (admin panel or `fallback off`); panels start again within ~10 s.

Before rolling back migration `0428_workspace_fallback`, clear active switches (servers keep their last file).
