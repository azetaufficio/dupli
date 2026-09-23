# Dupli

Central control plane for backing up Windows VMs (folders and PostgreSQL databases) to S3-compatible storage, built on [restic](https://restic.net) and .NET 10.

> **Status:** early development. Milestones M1 (agent), M2 (management API) and M3 (web UI) are implemented; not yet validated on production Windows VMs. See `docs/plan-m1-m3.md`.

## Components

- **Agent** (`src/Dupli.Agent`): Windows service and CLI, published as a single-file exe. It runs backup policies on a schedule and keeps secrets encrypted with DPAPI.
- **Agent.Core** (`src/Dupli.Agent.Core`): cross-platform backup engine. It includes the restic wrapper, a tool manager that downloads a pinned restic build and verifies its SHA-256, streaming `pg_dump` via `restic backup --stdin-from-command`, and error classification with retries.
- **Contracts** (`src/Dupli.Contracts`): DTOs shared between agent and server.
- **Server** (`src/Dupli.Server`, `.Domain`, `.Infrastructure`): ASP.NET Core control plane on PostgreSQL. Agents enroll with a one-time token and poll for typed jobs (backup, retention, repository check, restore test, restart); the server schedules them (cron + time zone), tracks leases/timeouts, escrows repository passwords and S3 keys, mirrors the pinned restic build and raises alerts by e-mail.
- **Web UI** (`src/Dupli.Web`): Angular app served by the server (BFF). Operators sign in with Microsoft Entra ID; the browser only holds an HttpOnly session cookie, and mutations carry an antiforgery token.

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

## Server

```
cp deploy/.env.example deploy/.env      # host, Postgres password, Entra ID app registration, SMTP
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
```

The image builds the web UI and the server; Caddy terminates TLS. Operator login is configured under `Dupli:Auth` (`EntraId`, or `Development` for local use only). The admin API also accepts an `X-Dupli-Admin-Key` header for automation when `Dupli:Admin:ApiKey` is set.

Local development: run PostgreSQL, then `dotnet run --project src/Dupli.Server` (Development environment: automatic login) and `npm start` in `src/Dupli.Web` (dev server with a proxy to the API on port 5000).

## Build and test

Requires the .NET SDK 10 (see `global.json`).

```
dotnet build
dotnet test tests/Dupli.Agent.Tests          # fast, runs anywhere (DPAPI tests skipped off Windows)
dotnet test tests/Dupli.Agent.Core.Tests     # needs Docker (PostgreSQL + RustFS) and a local pg_dump
dotnet test tests/Dupli.Server.Tests         # needs Docker (PostgreSQL)
dotnet test tests/Dupli.IntegrationTests     # server + agent in-process, needs Docker
cd src/Dupli.Web && npm ci && npm run build && npm test -- --watch=false
```

The end-to-end test looks for `pg_dump` in `DUPLI_TEST_PG_BIN` and in common install locations, and is skipped if it cannot find it.

## License

MIT, see `LICENSE`. Third-party components and their licenses are listed in `THIRD-PARTY-NOTICES.md`.
