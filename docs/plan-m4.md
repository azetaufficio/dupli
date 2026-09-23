# Dupli — Piano M4 (Update infrastructure)

## Context
M1-M3 done (see `docs/plan-m1-m3.md`). M4 of `plan.md` (§19-24, §34, §42): Launcher, Updater, version directories, restic manager, agent update, restic update, rollback. Decisions below were taken with the user and override `plan.md` where they conflict.

## Decisions
| Topic | Decision |
|---|---|
| Process model | **Launcher as supervisor.** The Windows service runs the Launcher; the Launcher starts the agent as a child process from `current.json`. The service never stops during an update: only the child changes. SCM recovery actions keep covering a Launcher crash |
| Updater | No separate Updater process. Switch, health check and rollback live in the Launcher. The Launcher itself is updated by re-running `install` (manual, rare) |
| Launcher binary | Same `dupli-agent.exe`, subcommand `launch`. `install` copies a stable instance to `%ProgramFiles%\Dupli\Launcher\dupli-agent.exe` (non-Windows: `{home}/launcher/`). One artifact to release and sign |
| Packages | GitHub Releases on tag `v*` (single-file win-x64, linux-x64, linux-arm64 + `.sha256`), version from the tag. Operator registers the release on the server (manual or "import from GitHub tag"); the server mirrors it like restic |
| Integrity | sha256 mandatory (from the server manifest over HTTPS). Authenticode optional: `agent.json` `update.requireSignature` + `update.signerThumbprints`, set at install; trust root is local, a compromised server cannot disable it. CI signing step only when signing secrets exist |
| Update trigger | **Desired state via heartbeat**, not jobs: the heartbeat response carries the desired agent and restic manifests; the agent applies them when idle (between poll cycles, no job running). Self-healing after offline periods. `JobType.AgentUpdate`/`ResticUpdate` stay unused |
| Target version | `agent.pinned_agent_version` ?? current release of the agent's channel for its platform (fallback dev -> beta -> stable). Downgrade allowed (desired is desired). Rollout % deferred (`plan.md` §24: manual is enough for MVP) |
| Channels | `dev`, `beta`, `stable` (default). Per agent, set from the UI |
| restic target | `agent.pinned_restic_version` ?? current restic release for the platform (no channels) |
| Version string | Single source: `AssemblyInformationalVersion` without `+commit` (supports `0.2.0-beta.1`). Only equality is compared. Fixes today's mismatch (install `ToString()` 4 parts vs heartbeat 3 parts) |

## Launcher (`dupli-agent launch`)
- Reads `versions\current.json` `{version, exePath, sha256, previous?, probation}`. Verifies the exe sha256 before every start (tampering in `ProgramData`).
- Starts `<exe> run` with `DUPLI_LAUNCHED=1`, stdin redirected. **Graceful stop = close stdin**: the child has a hosted service that stops the host on stdin EOF. Cross-platform, and a dead Launcher also stops the child. After 30 s the Launcher kills the process tree.
- Exit codes of the child: `0` during stop = done; `75` = restart (existing RestartAgent job); **`76` = apply `versions\pending.json`**; anything else = crash, restart with backoff (5 s, 30 s, 60 s cap).
- Switch: `pending.json` becomes `current.json` with `previous` = old current and `probation` = true (atomic write: temp + move). Window: 5 min from each child start, so probation survives a Launcher restart or reboot.
- Health: the child writes `versions\health.json` `{version, pid, at}` after its first poll cycle that completed, or that failed only for network reasons (server down must not cause a rollback). Healthy within the window = success.
- **Rollback**: crash or no health within the window: `current.json` back to `previous`, start it. Outcome written to `versions\update-state.json` `{version, outcome: Succeeded|RolledBack, error, at, failedVersions[]}`, reported by the agent in the heartbeat.
- Cleanup after a successful probation: keep current + previous, delete other `versions\*`.
- On Linux the same Launcher replaces the shell supervisor in `deploy/agent/entrypoint.sh` (SIGTERM to Launcher = close stdin to child), so update and rollback are exercised in the Aspire stack.

## Agent side
- **Agent update** (`Server/AgentUpdater`): desired version != running and not in `failedVersions` and launched by the Launcher: download to `versions\<ver>.partial\`, sha256, optional Authenticode, verify with `<exe> version` (new command, prints the version), move to `versions\<ver>\`, write `pending.json`, flush logs, exit 76. Not launched by the Launcher (dev, CLI): log "update available" and skip. A version rolled back is not retried until the desired version changes.
- **restic update**: desired restic != active: `ResticToolManager.EnsureInstalledAsync` (sha256 + `restic version` already there), then probe `cat config` on the repository with the new binary. Probe OK: switch the active manifest (`config\restic.json`, overrides the one from enrollment) and `ManagedResticBinaryProvider` uses it from the next job. Network failure: retry later. Other failure: stay on old, report error. Keep current + previous restic, delete older.
- Heartbeat adds: `platform`, `launcherManaged`, `lastUpdate` (from `update-state.json`), `resticUpdateError`.
- `install`: copies exe to `versions\<ver>\` and to the Launcher folder, `current.json` with sha256, service `binPath = "<launcher>" launch`. **ACL on the whole `ProgramData\Dupli`** (SYSTEM + Administrators full, Users read, no inheritance): today only `config\secrets` is restricted, but a user who can pre-create `versions\<future>\` could plant an exe. `install` without `--token` on an enrolled machine = repair/upgrade of layout, Launcher and service config (`sc config`). `uninstall` also removes the Launcher folder (data kept).

## Server side
- Migration `0004_m4.sql`: `agent.platform` (existing rows `windows_amd64`), `agent.channel` default `stable`, `agent.pinned_agent_version`, `agent.pinned_restic_version`, `agent.launcher_managed`, `agent.last_update_version|outcome|error|at`, `agent.restic_update_error`; `software_release.channel` (null for restic) and the "one current" index on `(product, platform, coalesce(channel, ''))`.
- `ReleaseMirror` generalizes `ResticMirror` by product. Agent download `GET /api/tools/agent/{version}/{platform}` (anonymous like restic). `Dupli:Releases:AllowInsecureSources` (dev/test only) accepts `http://` and `file://` sources.
- `DesiredVersionResolver` (channel fallback, pins, platform), used by the heartbeat.
- Admin API: `GET /api/admin/releases?product=`, `POST /releases/agent` (version, platform, channel, sourceUrl, sha256, makeCurrent), `POST /releases/agent/import` (tag + channel: builds GitHub URLs from `Dupli:Releases:GitHubRepository`, reads `.sha256` assets), `POST /releases/{id}/make-current`, `PUT /agents/{id}/update-settings` (channel, pins). `AgentDto` + `desiredAgentVersion`, `desiredResticVersion`, update status.
- Alert `AgentUpdateFailed` (rolled back on the desired version; resolved when running == desired). Agent not Launcher-managed shown in UI, no alert.

## Web UI
- Agents list: version with badge outdated / updating / update failed.
- Agent detail: "Updates" tab (channel, pins, desired vs running, last outcome).
- New "Releases" page: agent and restic releases per platform/channel, register/import, make current.

## CI / release
- `.github/workflows/release.yml` on tag `v*`: publish single-file win-x64, linux-x64, linux-arm64 with `-p:Version=${tag#v}`, assets `dupli-agent_<ver>_<platform>[.exe]` + `.sha256`, GitHub Release; server image tagged with the version. Optional signing step.
- `Directory.Build.props` keeps `0.1.0` as dev default.

## Steps
1. Contracts + version helper + heartbeat DTOs.
2. Server: migration, domain, resolver, heartbeat, mirror, admin API, alert. Tests.
3. Agent: `version` command, stdin shutdown, health writer, Launcher (supervisor state machine behind a process abstraction, unit tested with fakes + real child process test on Unix), AgentUpdater, restic switch, install/uninstall/ACL, optional Authenticode. Tests.
4. Release workflow + CI.
5. Aspire: entrypoint uses `launch`; manual e2e: build 0.1.1 linux, register it with a `file://` source, observe update; register a broken build, observe rollback + alert.
6. Web UI.
7. Docs (README, deploy guide: how to register a release) + HANDOFF.

## Exit gate (manual, Windows VM)
Install with Launcher (`sc qc DupliAgent` points to Program Files), update to a new release from the UI, rollback of a broken release, restic update, stop of the service during a backup (child stopped cleanly, job Interrupted), reboot.

## Deviations from plan.md
- No separate Updater (Launcher does it); no service stop/start during updates.
- Updates driven by heartbeat desired state instead of AgentUpdate/ResticUpdate jobs.
- Authenticode optional until a certificate exists.
- Progressive rollout deferred.
