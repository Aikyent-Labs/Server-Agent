# AikySpace one-way selective sync in the CloudOrc agent (R3)

Implements **AikySpace Enterprise Developer Specification v1.0 §08** inside the existing Control Agent
(no second privileged agent). Status: **written, NOT yet compiled or tested** - the machine it was
written on had no .NET SDK. First step for a developer: `dotnet build` + `dotnet test`.

## What it does

The customer picks folders on their server in the CloudOrc portal. CloudOrc sends a signed typed
command `sync.policy.apply`. The agent then keeps those folders copied into AikyDrive, one way:

```
scan folder (hash only changed files) → manifest → CloudOrc → AikySpace: "upload these"
   → part grants (signed URLs) → bytes go straight to storage → complete → checkpoint
```

- Same path + same content never creates a new version. Changed content = new version of the same document.
- A file deleted on the server is only *tombstoned* in AikyDrive. Cloud history and backups stay.
- Backup (Kopia) is untouched and runs independently. Sync is not backup.

## Code map

| File | Role |
|---|---|
| `Contracts/Sync/SyncContracts.cs` | command types, capability `sync.v1`, states, routes, snake_case HTTPS DTOs |
| `ControlAgent/Sync/SyncPathPolicy.cs` | canonical paths, blocks system/program/AppData/agent folders, drive roots, junction escapes, shared-server roots; exclusion globs |
| `ControlAgent/Sync/SyncDatabase.cs`, `SyncStore.cs` | own `sync.db` (rollback-safe: the main state DB refuses newer schemas) |
| `ControlAgent/Sync/SyncScanner.cs` | inventory, SHA-256 with read throttling, locked / changed-during-read detection |
| `ControlAgent/Sync/SyncTransport.cs` | control-plane calls + direct part PUTs (https only, rate limited) |
| `ControlAgent/Sync/SyncCoordinator.cs` | per-root state machine, file watcher hints, retries, checkpoints |
| `ControlAgent/Commands/SyncCommandHandlers.cs` | `sync.policy.apply/remove`, `sync.pause/resume/rescan/revoke` |

Wiring: `Program.cs` (Sync section), `TypedCommandDispatcher` routes `sync.*`, `AgentCapabilitiesProvider`
advertises `sync.v1`, `BackendApiClient.GetAsync` (grant refresh). Settings: `appsettings.json` → `Sync`.

## State machine

`IDLE → SCANNING → UPLOADING → VERIFYING → CHECKPOINTED → IDLE`
Recoverable: `PAUSED_POLICY`, `WAITING_NETWORK`, `RETRY_BACKOFF`, `FILE_LOCKED`, `QUOTA_BLOCKED`.
Terminal: `REVOKED`, `INVALID_ROOT` (`COMPROMISED` is reserved for an operator action).

Decisions are made on the HTTP status (413 quota / file too large, 410 revoked, 404 unknown root,
409 paused, 5xx/network transient); CloudOrc may normalise error codes.

## Safety rules

1. A scan that could not read every file is never sent with `complete=true`. Completion tombstones
   what was not seen, so a locked Tally file is never tombstoned by mistake.
2. A file is checkpointed locally only after AikySpace confirmed the commit.
3. A file that changed during hashing or upload is not committed. The next pass picks it up.
4. No command executes code. Sync commands only change which folders are read.
5. The CloudOrc credential is never sent to storage. Part URLs carry their own short-lived grant.
6. While a protected process (default `tally`) runs, read and upload budgets drop to 25%.

## Bandwidth, file identity, telemetry

- `SyncBandwidth.ActiveWindow` (Sync/SyncHelpers.cs): first policy window matching local day + time
  (over-midnight windows supported, `24:00` end). Window `maxUploadMbps` overrides the policy cap;
  `0` = hold uploads (a 16 KB/s trickle keeps sessions alive). Protected apps running still quarter the rate.
- `StableFileId.TryGet`: NTFS volume serial + 64-bit file index (`GetFileInformationByHandle`), sent as
  `file_id` in the manifest so AikySpace can recognise renames. Null on non-NTFS / failure.
- Checkpoint stats add `pendingUploads`, `lagSeconds`, `uploadBudgetBps`.

## Backend contract (CloudOrc)

`POST api/v1/sync/manifests`, `POST api/v1/sync/uploads`, `GET api/v1/sync/uploads/{id}?root_id=`,
`POST api/v1/sync/uploads/{id}/complete`, `POST api/v1/sync/checkpoints`. Same credential/mTLS as
backup v2 (`requireAgent`). The VPS also needs outbound HTTPS to the storage host (part URLs).

## Tests

`tests/CloudOrc.ControlAgent.Tests/Sync/SyncTests.cs` (path policy, globs, store, scanner incl. locked
file, status mapping, bandwidth windows, stable file id). PASS on 2026-11-18 (44 sync tests; whole solution 647/647, Debug + Release build OK).
A corrupt or locked sync.db is moved aside / kept in memory - sync can never stop the agent from starting.
