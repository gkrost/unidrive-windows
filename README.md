# unidrive-windows

The **Windows desktop tier** for [unidrive](https://github.com/gkrost/unidrive) — a Cloud Files API (CfAPI) mount client that presents a cloud account as on-demand placeholder files in Explorer.

It is a **separate app** (per the unidrive "platforms are separate apps" rule) that consumes the unidrive engine over the daemon's AF_UNIX NDJSON IPC — the Windows twin of the Linux FUSE co-daemon. Design docs live in the main repo:

- Plan: [`docs/dev/plans/win11-cfapi-mount.md`](https://github.com/gkrost/unidrive/blob/main/docs/dev/plans/win11-cfapi-mount.md)
- Phase 0/1 spec: [`docs/dev/specs/win11-cfapi-phase01.md`](https://github.com/gkrost/unidrive/blob/main/docs/dev/specs/win11-cfapi-phase01.md)

## Status — Phase 1.3 (IPC + CfAPI registration + FETCH_PLACEHOLDERS + FETCH_DATA)

| Phase | Module | Status |
|-------|--------|--------|
| 0.1 | `Unidrive.Recovery` — orphan-recovery tool (scan/revert/unregister/detach) | ✅ |
| 0.3 | `Unidrive.Ipc` — AF_UNIX NDJSON client, pool, subscribe stream | ✅ |
| 1.1 | `Unidrive.CfApi` — sync root registration (`CfRegisterSyncRoot`, `CfConnectSyncRoot`) | ✅ |
| 1.6 | Teardown (`CfDisconnectSyncRoot` + revert placeholders + `CfUnregisterSyncRoot`) | ✅ |
| 1.2 | FETCH_PLACEHOLDERS callback → `hydration.list` → `CfCreatePlaceholders` | ✅ |
| 1.3 | FETCH_DATA callback → `hydration.open_read` → `CfExecute(TRANSFER_DATA)` | ✅ |
| 1.5 | Live refresh via subscribe stream | code landed — live verification open (#4) |
| 1.4 | Dehydrate / free up space | code landed — live verification open (#3) |
| 2–4 | Writeback (#5), shell UX (#6), packaging (#7) | ⬜ post-MVP (except minimal install story, #7) |

**MVP boundary** (decision [gkrost/unidrive#290](https://github.com/gkrost/unidrive/issues/290)): the **read-only tier** — Phases 0–1, including live verification of 1.4/1.5 (#3/#4) — is in the unidrive MVP, plus a minimal install story (#7). The release gate for this surface is #9, applying the shared acceptance criteria ([`docs/dev/specs/mvp-acceptance-criteria.md`](https://github.com/gkrost/unidrive/blob/main/docs/dev/specs/mvp-acceptance-criteria.md)). Writeback, shell UX, and MSIX/Authenticode packaging are post-MVP.

## Build & run

Requires the **.NET 8 SDK**.

```pwsh
dotnet build
dotnet test

# Phase 0.3 smoke: start a unidrive JVM daemon first, then ask it for status.
#   java -jar unidrive.jar -p <profile> daemon run
dotnet run --project src/Unidrive.Cli -- status --profile <profile>
# -> connecting to C:\Users\...\Temp\unidrive-ipc\unidrive-<profile>.sock ...
# -> OK - daemon up <N> ms, <K> client(s), refresh_in_flight=False, job=-

# Phase 1.1: mount the sync root (requires daemon running)
dotnet run --project src/Unidrive.Cli -- mount --profile <profile> --root <path>

# Phase 0.1: recover orphaned placeholders
dotnet run --project src/Unidrive.Recovery -- scan --path <dir>
dotnet run --project src/Unidrive.Recovery -- clean --path <dir>
```

## Layout

| Path | Role |
|------|------|
| `src/Unidrive.Ipc` | NDJSON IPC client: socket-path resolution, connection pool, request/reply, subscribe stream |
| `src/Unidrive.Cli` | `unidrive-win` — CLI with `status`, `mount`, `unmount` commands |
| `src/Unidrive.CfApi` | CfAPI (`cldflt`) P/Invoke bindings, `SyncRootManager`, callback dispatcher |
| `src/Unidrive.Recovery` | Orphan-placeholder recovery tool (`scan`, `revert`, `unregister`, `clean`, `detach`) |
| `tests/Unidrive.Ipc.Tests` | unit tests (socket-path logic, daemon.status round-trips; no daemon required) |

## Decisions (recorded in the unidrive plan)

- Language: **C# / .NET 8 + WinUI** (best CfAPI + Explorer-shell ergonomics).
- Deployment: **IPC to the JVM daemon now**, in-process engine embed later (post the tracking-set engine, unidrive#99).
- Phase 1 packaging: **unpackaged `CfRegisterSyncRoot`** (MSIX deferred to the shell-UX phase).
- Lifecycle: the client **is** the Windows Service and supervises the JVM daemon.
