# Secure Access — Windows Credential Provider shim (handoff v1.2 §29–30)

Status (25 Nov 2026): **broker side built and tested; the native shim is not built yet.**
The build server has no C++ toolchain (no Visual Studio / Build Tools / Windows SDK), so the shim
DLL could not be compiled here. Everything the shim talks to already exists and is tested.

## What exists

| Piece | Where | State |
|---|---|---|
| Backend decision | `POST /api/v1/remote-workspace/agent/secure-access/decision` (agent-authenticated) — `Cloude_Website/backend/src/modules/remoteWorkspace/remoteWorkspace.security.js` | Built, unit-tested, live-tested (ALLOW, MFA_REQUIRED, NOT_MANAGED) |
| Policy sources | `secureAccess.evaluateSecureAccess` (IP policy, fail-closed, audit) + `rdpMfa` (seat 2FA + remember window) | Existing CloudOrc truth, reused |
| Secure-Access Broker | `CloudOrc.ControlAgent/Workspace/SecureAccessBroker.cs` — pipe `\\.\pipe\CloudOrc.SecureAccess` (SYSTEM + Administrators, network denied) | Built, unit-tested, live-tested |
| Contract | `CloudOrc.Agent.Contracts/Workspace/SecureAccessProtocol.cs` | Built |
| In-session OTP gate (fallback that already enforces 2FA for every client) | `backend/src/modules/rdpMfa` (CloudOrcOtpGate) | Existing, unchanged |

## Pipe contract (one JSON line each way)

Request: `{"action":"decide","username":"DOMAIN\\user","sessionId":3,"clientIp":"203.0.113.10"}`

Response: `{"allow":false,"decision":"MFA_REQUIRED","reasonCode":"MFA_REQUIRED","mfaRequired":true,"policyVersion":3,"expiresAt":"…","correlationId":"…","fromCache":false}`

`decision` is one of `ALLOW`, `MFA_REQUIRED`, `DENY`, `NOT_MANAGED` (Windows decides — e.g. the
server's own administrator), `UNAVAILABLE` (CloudOrc unreachable for a known managed seat → fail closed).
Offline grace for a recent ALLOW is configurable (`Workspace:SecureAccessOfflineGraceMinutes`, default 0 = off).

## The shim to build (C++, COM, minimal)

- Wrap the in-box password provider (`ICredentialProviderFilter` + wrapped `ICredentialProviderCredential`), so
  Windows still validates the password. No Winlogon patching, no password interception or storage.
- After Windows accepts the password, call the broker pipe with the user name, session and client address
  (`WTSQuerySessionInformation` WTSClientAddress). 3 s connect / 15 s total timeout.
- `ALLOW` / `NOT_MANAGED` → continue logon. `DENY` / `UNAVAILABLE` → fail the logon with the reason text.
  `MFA_REQUIRED` → show a code field; verify through the broker (add `{"action":"verify","code":…}` to the
  broker — it forwards to the existing rdpMfa verify) and only then continue.
- No .NET in LogonUI, no network calls from the shim, no secrets in its registry/config.

## Build, install and recovery (must be done on a test VM first)

1. Build x64 Release with Visual Studio 2022 Build Tools + Windows SDK; sign the DLL.
2. Install: copy the DLL to `System32`, register the CLSID under
   `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Provider Filters`.
3. **Recovery path (documented before any pilot):** boot to Safe Mode (credential providers other than
   in-box are not loaded) and delete the filter key, or remove it remotely via the agent's signed command.
   Never install on a server whose only access is RDP without console/hypervisor access available.
4. Feature flags: `secure_access_broker` (broker answers) and `native_credential_provider` (shim installed),
   pilot cohort only.
