# Dupli

Central control plane for backing up Windows VMs (folders and PostgreSQL databases) to S3-compatible storage, built on [restic](https://restic.net) and .NET 10.

> **Status:** early development. Milestones M1 (agent), M2 (management API), M3 (web UI) and M4 (remote updates with rollback) are implemented; not yet validated on production Windows VMs. See `docs/plan-m1-m3.md` and `docs/plan-m4.md`.

## Components

- **Agent** (`src/Dupli.Agent`): Windows service and CLI, published as a single-file exe. It runs backup policies on a schedule and keeps secrets encrypted with DPAPI. The service runs the same exe as **Launcher** (`launch`), which supervises the agent process, switches to a new version staged by the agent and rolls back when the new version crashes or does not report healthy within 5 minutes.
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
dupli-agent launch                                # what the service runs: Launcher supervising `run`
dupli-agent run                                   # agent process (server polling or local scheduler)
dupli-agent version
dupli-agent backup --policy <id>
dupli-agent snapshots [--tag k=v]
dupli-agent restore --snapshot <id> [--target <dir>] [--include <path>]
dupli-agent forget --policy <id> [--prune]
dupli-agent check [--subset 5%]
dupli-agent secret set <name>                     # value read from stdin
dupli-agent install --server <url> --token <t> [--require-signature] [--signer-thumbprint <sha1>]
dupli-agent install                               # enrolled machine: reinstall binaries, Launcher and service
dupli-agent uninstall
```

The data root is `%ProgramData%\Dupli`, and you can override it with `DUPLI_HOME`. The configuration lives in `config\agent.json`; agent versions live side by side in `versions\<ver>\` and `versions\current.json` says which one the Launcher starts. The Launcher itself is a copy in `%ProgramFiles%\Dupli\Launcher`.

Updates: the server tells each agent, in the heartbeat response, which agent and restic release it should run (release channel `dev`/`beta`/`stable` or a pinned version). The agent downloads it while idle, checks its SHA-256 (and, with `--require-signature`, its Authenticode signature), and hands over to the Launcher. Releases are published by tagging `vX.Y.Z` (`.github/workflows/release.yml`) and registered on the server from the *Releases* page.

## Server

```
cp deploy/.env.example deploy/.env      # host, Postgres password, Entra ID app registration, SMTP
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
```

The image builds the web UI and the server; Caddy terminates TLS. Operator login is configured under `Dupli:Auth` (`EntraId`, or `Development` for local use only). The admin API also accepts an `X-Dupli-Admin-Key` header for automation when `Dupli:Admin:ApiKey` is set.

Local development: run PostgreSQL, then `dotnet run --project src/Dupli.Server` (Development environment: automatic login) and `npm start` in `src/Dupli.Web` (dev server with a proxy to the API on port 5000).

## Local test stack (Aspire)

`src/Dupli.AppHost` starts everything needed to exercise the server end to end without Azure or a Windows VM. Requirements: Docker, Node.js, .NET SDK 10.

```
ASPIRE_ALLOW_UNSECURED_TRANSPORT=true dotnet run --project src/Dupli.AppHost --launch-profile http
```

| Resource | What it is |
|---|---|
| `postgres` | PostgreSQL: server database `dupli` + `sampledb` backed up by the agent |
| `mailpit` | SMTP catcher for alert e-mails (web UI on its `http` endpoint) |
| `rustfs` | S3-compatible storage for the restic repositories |
| `server` | Management server on http://localhost:5000 (Development login, admin key `aspire-dev-admin-key`) |
| `web` | Angular dev server on http://localhost:4200 |
| `agent` | Linux container (`deploy/agent/Dockerfile`) running the agent |

On first start the agent container registers itself through the admin API, enrolls, creates a `demo` policy (sample files + PostgreSQL) and queues a backup. The container runs the same Launcher as the Windows service, so restart, update and rollback work as on Windows. Everything is ephemeral: each run starts from an empty database and bucket. The Linux agent is a test vehicle only. On Linux, secrets are protected by file permissions (0600), not DPAPI, and enrollment over plain HTTP needs `DUPLI_ALLOW_INSECURE_HTTP=true`.

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
