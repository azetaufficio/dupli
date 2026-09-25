# Dupli

Dupli is a centralized control plane for backing up Windows VMs — folders and PostgreSQL databases — to S3-compatible storage, built on [restic](https://restic.net) and .NET 10.

> **Status:** in production. Milestones M1–M5 (agent, management API, web UI, remote updates with rollback, credentials/notifications/UI gaps) are implemented and running: server deployed on Azure Container Apps, agents enrolled on Windows VMs, backup and restore (files and PostgreSQL) verified end to end.

## Why Dupli

Most teams running a handful of Windows VMs with PostgreSQL databases end up stitching backups together from scheduled tasks, ad-hoc scripts and a cron job somewhere calling `pg_dump`. The commercial alternatives (Veeam, Acronis, …) are closed-source, licensed per agent, and overkill for "back up these folders and databases to my own S3 bucket, alert me if something breaks." We couldn't find a comparable **fully open source** option that combined a central console, scheduled policies, S3-agnostic storage and remote agent management — so we built one.

## What it does

- **Central console**: register agents, define backup policies (folders and/or PostgreSQL instances), and see status, history and logs from one web UI.
- **Backup engine**: [restic](https://restic.net) under the hood — deduplicated, encrypted, incremental snapshots. Folders are backed up directly; PostgreSQL databases are dumped and streamed straight into restic (no temporary files), with per-database and per-cluster ("globals") dumps discovered automatically.
- **Any S3-compatible storage**: Wasabi, Backblaze B2, AWS S3, self-hosted RustFS/MinIO — one restic repository per VM.
- **Restore**: browse snapshots and restore folders or databases from the web UI.
- **Remote agent management**: agents enroll with a one-time token, poll the server for jobs, and can be updated remotely (server-driven version rollout with automatic rollback if the new version doesn't come up healthy).
- **Alerting & notifications**: e-mail (SMTP or Microsoft Graph/Office 365) and in-app notifications, configurable per operator and per alert type (failed backups, offline agents, outdated agents, failed updates…).
- **Access control**: operators sign in with Microsoft Entra ID; roles are Owner, Operator and Viewer.

## Components

- **Agent** (`src/Dupli.Agent`): Windows service and CLI, published as a single-file exe, always driven by the server (no standalone mode). It persists only its own identity (`agent-secret`, DPAPI-encrypted); the repository password, S3 keys and PostgreSQL passwords are fetched just in time for each job and released in memory when the job ends. The service runs the same exe as **Launcher** (`launch`), which supervises the agent process, switches to a new version staged by the agent and rolls back when the new version crashes or does not report healthy within 5 minutes.
- **Agent.Core** (`src/Dupli.Agent.Core`): cross-platform backup engine. It includes the restic wrapper, a tool manager that downloads a pinned restic build and verifies its SHA-256, streaming `pg_dump` via `restic backup --stdin-from-command`, and error classification with retries.
- **Contracts** (`src/Dupli.Contracts`): DTOs shared between agent and server.
- **Server** (`src/Dupli.Server`, `.Domain`, `.Infrastructure`): ASP.NET Core control plane on PostgreSQL. Agents enroll with a one-time token and poll for typed jobs (backup, retention, repository check, restore test, restart); the server schedules them (cron + time zone), tracks leases/timeouts, escrows repository passwords, S3 keys and PostgreSQL connection passwords (handed to the agent per job, never persisted by it), mirrors the pinned restic build and raises alerts by e-mail.
- **Web UI** (`src/Dupli.Web`): Angular app served by the server (BFF). Operators sign in with Microsoft Entra ID; the browser only holds an HttpOnly session cookie, and mutations carry an antiforgery token.

Design choices:
- One restic repository per VM. Snapshots are tagged `policy=`, `source=`, `type=dir|pg`, `db=`.
- PostgreSQL dumps stream straight into restic, with no temporary files. All databases are discovered automatically, and globals are dumped with `pg_dumpall --globals-only`.
- The code is agnostic about the S3 provider (Wasabi, Backblaze B2, AWS, RustFS/MinIO…).

## Agent CLI

```
dupli-agent launch                                # what the service runs: Launcher supervising `run`
dupli-agent run                                   # agent process, server-driven (fails if not enrolled)
dupli-agent version
dupli-agent rotate-secret                         # rotates the agent secret used to authenticate with the server
dupli-agent install --server <url> --token <t> [--require-signature] [--signer-thumbprint <sha1>]
dupli-agent install                               # enrolled machine: reinstall binaries, Launcher and service
dupli-agent uninstall
```

There is no standalone mode and no local operator commands (`backup`/`snapshots`/`restore`/`forget`/`check`/`secret set`): the agent only ever runs jobs assigned by the server, and PostgreSQL connection passwords are set from the web UI (connection form) instead of `dupli-agent secret set`.

The data root is `%ProgramData%\Dupli`, and you can override it with `DUPLI_HOME`. The configuration lives in `config\agent.json`; agent versions live side by side in `versions\<ver>\` and `versions\current.json` says which one the Launcher starts. The Launcher itself is a copy in `%ProgramFiles%\Dupli\Launcher`.

Updates: the server tells each agent, in the heartbeat response, which agent and restic release it should run (release channel `dev`/`beta`/`stable` or a pinned version). The agent downloads it while idle, checks its SHA-256 (and, with `--require-signature`, its Authenticode signature), and hands over to the Launcher. Releases are published by tagging `vX.Y.Z` (`.github/workflows/release.yml`) and registered on the server from the *Releases* page.

## Server

```
cp deploy/.env.example deploy/.env      # host, Postgres password, Entra ID app registration, SMTP
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
```

See [`docs/deploy-docker-compose.md`](docs/deploy-docker-compose.md) for a full walkthrough (prerequisites, TLS, backing up the stack itself), or [`docs/deploy-azure-container-apps.md`](docs/deploy-azure-container-apps.md) for a managed deployment on Azure Container Apps.

The image builds the web UI and the server; Caddy terminates TLS. Operators sign in with Microsoft Entra ID (`Dupli:Auth`, in every environment). Who may sign in is the operator user table, managed from the **Users** page by an `Owner` (roles: `Owner`, `Operator`, `Viewer`). While the table is empty only `Dupli:Auth:BootstrapOwnerEmail` can sign in, and becomes the first owner; the server refuses to start in EntraId mode when neither exists. The admin API also accepts an `X-Dupli-Admin-Key` header for automation when `Dupli:Admin:ApiKey` is set; the same key opens the `/admin` break-glass page (15-minute session, user management only).

Local development: register a dev Entra ID app (platform Web, redirect URIs `http://localhost:4200/signin-oidc` and `http://localhost:5000/signin-oidc`) and store its settings in the server's user secrets:

```
dotnet user-secrets --project src/Dupli.Server set Dupli:Auth:EntraId:TenantId <tenant-id>
dotnet user-secrets --project src/Dupli.Server set Dupli:Auth:EntraId:ClientId <client-id>
dotnet user-secrets --project src/Dupli.Server set Dupli:Auth:EntraId:ClientSecret <secret>
dotnet user-secrets --project src/Dupli.Server set Dupli:Auth:BootstrapOwnerEmail <you@yourdomain>
```

Then run PostgreSQL, `dotnet run --project src/Dupli.Server` and `npm start` in `src/Dupli.Web` (dev server with a proxy to the API on port 5000). Sign-in over plain HTTP works on `localhost` in Chrome/Edge/Firefox.

## Local test stack (Aspire)

`src/Dupli.AppHost` starts everything needed to exercise the server end to end without Azure or a Windows VM. Requirements: Docker, Node.js, .NET SDK 10.

```
ASPIRE_ALLOW_UNSECURED_TRANSPORT=true dotnet run --project src/Dupli.AppHost --launch-profile http
```

The server signs in with the same dev Entra ID app as above; set the AppHost parameters once (the dashboard asks for missing ones):

```
dotnet user-secrets --project src/Dupli.AppHost set Parameters:entra-tenant-id <tenant-id>
dotnet user-secrets --project src/Dupli.AppHost set Parameters:entra-client-id <client-id>
dotnet user-secrets --project src/Dupli.AppHost set Parameters:entra-client-secret <secret>
dotnet user-secrets --project src/Dupli.AppHost set Parameters:bootstrap-owner-email <you@yourdomain>
```

| Resource | What it is |
|---|---|
| `postgres` | PostgreSQL: server database `dupli` + `sampledb` backed up by the agent |
| `mailpit` | SMTP catcher for alert e-mails (web UI on its `http` endpoint) |
| `rustfs` | S3-compatible storage for the restic repositories |
| `server` | Management server on http://localhost:5000 (Entra ID login with the parameters below, admin key `aspire-dev-admin-key`) |
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
