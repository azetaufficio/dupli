# Dupli

Central control plane for backing up Windows VMs (folders and PostgreSQL databases) to S3-compatible storage, built on [restic](https://restic.net) and .NET 10.

> **Status:** early development. Milestone M1 (standalone agent) is implemented; the management server (M2) and web UI (M3) are planned. See `docs/plan-m1-m3.md`.

## Components

- **Agent** (`src/Dupli.Agent`): Windows service and CLI, published as a single-file exe. It runs backup policies on a schedule and keeps secrets encrypted with DPAPI.
- **Agent.Core** (`src/Dupli.Agent.Core`): cross-platform backup engine. It includes the restic wrapper, a tool manager that downloads a pinned restic build and verifies its SHA-256, streaming `pg_dump` via `restic backup --stdin-from-command`, and error classification with retries.
- **Contracts** (`src/Dupli.Contracts`): DTOs shared between agent and server.

Design choices:
- One restic repository per VM. Snapshots are tagged `policy=`, `source=`, `type=dir|pg`, `db=`.
- PostgreSQL dumps stream straight into restic, with no temporary files. All databases are discovered automatically, and globals are dumped with `pg_dumpall --globals-only`.
- The code is agnostic about the S3 provider (Wasabi, Backblaze B2, AWS, RustFS/MinIO…).

## Agent CLI

```
dupli-agent run                                   # run as service / foreground scheduler
dupli-agent backup --policy <id>
dupli-agent snapshots [--tag k=v]
dupli-agent restore --snapshot <id> [--target <dir>] [--include <path>]
dupli-agent forget --policy <id> [--prune]
dupli-agent check [--subset 5%]
dupli-agent secret set <name>                     # value read from stdin
dupli-agent install --server <url> --token <t>
dupli-agent uninstall
```

The data root is `%ProgramData%\Dupli`, and you can override it with `DUPLI_HOME`. The configuration lives in `config\agent.json`.

## Build and test

Requires the .NET SDK 10 (see `global.json`).

```
dotnet build
dotnet test tests/Dupli.Agent.Tests          # fast, runs anywhere (DPAPI tests skipped off Windows)
dotnet test tests/Dupli.Agent.Core.Tests     # needs Docker (PostgreSQL + RustFS) and a local pg_dump
```

The end-to-end test looks for `pg_dump` in `DUPLI_TEST_PG_BIN` and in common install locations, and is skipped if it cannot find it.

## License

MIT, see `LICENSE`. Third-party components and their licenses are listed in `THIRD-PARTY-NOTICES.md`.
