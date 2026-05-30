# unidrive-windows

The **Windows desktop tier** for [unidrive](https://github.com/gkrost/unidrive) — a Cloud Files API (CfAPI) mount client that presents a cloud account as on-demand placeholder files in Explorer.

It is a **separate app** (per the unidrive "platforms are separate apps" rule) that consumes the unidrive engine over the daemon's AF_UNIX NDJSON IPC — the Windows twin of the Linux FUSE co-daemon. Design docs live in the main repo:

- Plan: [`docs/dev/plans/win11-cfapi-mount.md`](https://github.com/gkrost/unidrive/blob/main/docs/dev/plans/win11-cfapi-mount.md)
- Phase 0/1 spec: [`docs/dev/specs/win11-cfapi-phase01.md`](https://github.com/gkrost/unidrive/blob/main/docs/dev/specs/win11-cfapi-phase01.md)

## Status — Phase 0.3 (IPC client skeleton)

`Unidrive.Ipc` speaks the daemon's newline-delimited-JSON protocol over a Unix-domain socket
(`%TEMP%/unidrive-ipc/unidrive-<profile>.sock`), replicating `IpcServer.defaultSocketPath` exactly,
with the connection-pool + subscribe model from the spec. `Unidrive.Cli` round-trips `daemon.status`
as the Phase 0.3 acceptance smoke test.

Phase 1+ (not yet here): `Unidrive.CfApi` (the `cldflt` callback binding) and `Unidrive.WinHost`
(the Windows Service that supervises the JVM daemon + owns the CfAPI sync root).

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
```

## Layout

| Path | Role |
|------|------|
| `src/Unidrive.Ipc` | NDJSON IPC client: socket-path resolution, connection pool, request/reply, subscribe stream |
| `src/Unidrive.Cli` | `unidrive-win` — the `daemon.status` smoke client (Phase 0.3) |
| `tests/Unidrive.Ipc.Tests` | unit tests (socket-path logic; no daemon required) |
| `src/Unidrive.CfApi` *(later)* | the CfAPI (`cldflt`) callback ↔ hydration-verb binding (Phase 1) |
| `src/Unidrive.WinHost` *(later)* | the Windows Service host + lifecycle (Phase 4) |

## Decisions (recorded in the unidrive plan)

- Language: **C# / .NET 8 + WinUI** (best CfAPI + Explorer-shell ergonomics).
- Deployment: **IPC to the JVM daemon now**, in-process engine embed later (post the tracking-set engine, unidrive#99).
- Phase 1 packaging: **unpackaged `CfRegisterSyncRoot`** (MSIX deferred to the shell-UX phase).
- Lifecycle: the client **is** the Windows Service and supervises the JVM daemon.
