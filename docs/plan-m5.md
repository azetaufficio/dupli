# Dupli — Piano M5 (Restore)

## Context
M1-M4 done (see `docs/plan-m1-m3.md`, `docs/plan-m4.md`). M5 of `plan.md` (§25, §26, §43): snapshot list, browse snapshot, restore directory, restore PostgreSQL dump. Decisions below were taken with the user and override `plan.md` where they conflict. Also in scope: selectable database selection mode for PostgreSQL sources.

## Decisions
| Topic | Decision |
|---|---|
| Snapshot list + browse | **Run by the server**, read-only: the server decrypts the escrowed repository password + S3 key and runs `restic snapshots` / `restic ls` with `--no-lock`. Instant, works with the VM offline or busy with a long backup (the disaster-recovery case). The server already holds the credentials, so the theoretical exposure does not change; it now uses them actively |
| restic on the server | Current restic release for the server platform (`linux_amd64`/`linux_arm64` seeded), installed from the release mirror cache (sha256 + `restic version`, via `ResticToolManager`, `file://` source). Override `Dupli:Restore:ResticPath` for dev/tests. No release for the platform = 503 with a clear message |
| S3 endpoint seen by the server | Same storage target as the agents; `Dupli:Restore:EndpointOverrides` (`From`/`To`) when the server reaches S3 through another URL (private endpoint; container network name in the Aspire stack) |
| Restore execution | Always a **Restore job on the agent** (it writes on the VM). One pending restore per agent (coalescing, 409 otherwise) |
| Restore target | Default `C:\DupliRestore\<job-id>\` (non-Windows `{home}/tmp/restore/<job-id>`). Optional custom target: must not exist or be empty, and `RestoreGuard` rejects any overlap with a backed-up path (policies from the payload + the agent's cached policies). Never overwrites production data |
| Selection | Whole snapshot or a list of paths (files/directories, `restic restore --include`) |
| PostgreSQL | Default: the `.dump` (or `globals.sql`) is restored as a file in the target. Option **"restore into a new database"** with explicit confirmation (the operator retypes the new name): the agent checks the database does not exist, `CREATE DATABASE`, then `pg_restore --dbname <new>` (owners and privileges kept: same cluster, roles exist). `pg_restore` exit != 0 = SucceededWithWarnings with the stderr tail. Never `DROP`, never into an existing database, globals never applied automatically |
| PostgreSQL connection for restore | Taken from the policy source the snapshot belongs to (`policy=`/`source=` tags), sent in the payload. Policy deleted = only file restore |
| Result | `JobItemResultDto.Location`: restored directory (and database name) shown in the UI |

## Database selection (PostgreSQL source)
- `PostgresSourceDto.DatabaseSelection`: `AllExcept` (default, today's behaviour: all connectable non-template databases except `postgres` and `ExcludeDatabases`) or `Only` (`IncludeDatabases`, exactly those; `postgres` allowed explicitly).
- `Only`: a listed database missing on the server fails that source (Permanent): a silent miss would look like a successful backup. Server validation: `Only` needs at least one name.
- Backward compatible: old specs without the field = `AllExcept`. An agent older than M5 ignores the new fields (dumps all except excluded): update agents before using `Only`.

## Server
- `Restore/RepositoryBrowser`: builds `RepositoryTarget` for an agent (endpoint/bucket/prefix/region + escrow), restic cache under `Dupli:Restore:CachePath` (default `/var/lib/dupli/cache`), global concurrency limit, snapshot list cached 60 s per agent (`?refresh=true` bypasses).
- Admin API:
  - `GET /api/admin/agents/{id}/snapshots` — id, time, host, paths, tags + parsed `policyId`, `sourceId`, `type` (dir|pg), `database`.
  - `GET /api/admin/agents/{id}/snapshots/{snapshotId}/tree?path=/…` — direct children (name, path, type, size, mtime).
  - `POST /api/admin/agents/{id}/restores` `{snapshotId, includes[], targetDirectory?, newDatabase?}` — validates the snapshot exists in the agent's repository, includes are absolute restic paths without `..`, new database only for a `type=pg` snapshot of a still existing policy source, name `^[A-Za-z_][A-Za-z0-9_-]{0,62}$` and different from the source database. Creates a Restore job (payload carries the policies for the guard).

## Agent
- `RestoreJobPayload` (`kind: restore`): `SnapshotId`, `Includes`, `TargetDirectory?`, `Postgres?` (`Source` + `Database` + `NewDatabase`), `Policies`.
- `JobExecutor.RestoreAsync`: guard + empty-target check, `restic restore`, then optional `PostgresRestorer` (Npgsql existence check, `CREATE DATABASE`, `pg_restore` from `IPostgresBinLocator.Newest` or the source `BinDirectory`).

## Web UI
- Agent detail, tab **Snapshots**: list with filters (policy, source, type), browse with breadcrumbs and checkboxes, "Restore selected" / "Restore all" dialog (optional target; for PG snapshots "restore into new database" with retype confirmation), link to the job.
- Policy editor, PostgreSQL source: radio "all except" / "only these" with the matching list.

## Tests
- Agent.Core: `PostgresDumpProvider` selection (`Only`, missing database), `ResticBackupEngine.ListDirectoryAsync`, e2e restore into new database (PG + RustFS).
- Server: snapshots/tree/restore API against a real local repository (restic binary from the test fixture), validation errors, coalescing.
- Integration: backup, browse from server, restore job executed by the agent, file content checked.

## Out of scope
- In-place restore / overwrite of production paths or databases (spec §25-26).
- Download of single files through the browser.
- Restore of an agent's snapshots onto a different VM.
