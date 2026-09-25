import { HttpContext, httpResource } from '@angular/common/http';
import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import {
  Agent,
  AGENT_CHANNELS,
  AgentChannel,
  Alert,
  EnrollmentToken,
  Job,
  LogEntry,
  PgConnection,
  PgConnectionRequest,
  Policy,
  Release,
  Run,
  StorageTarget,
  SystemJobType,
  UpdateAgentSettingsRequest,
  UpdateAgentStorageCredentialsRequest,
} from '../core/models';
import { ToastService } from '../core/toast.service';
import { Badge } from '../shared/badge';
import { ConfirmService } from '../shared/confirm';
import { BytesPipe, DateTimePipe, DurationPipe, RelativeTimePipe } from '../shared/format';
import { PagedList } from '../shared/paged-list';
import { SnapshotBrowser } from '../shared/snapshot-browser';
import { AlertsTable, ItemsTable, JobsTable, LogsTable, RunsTable, lookup } from '../shared/tables';

type Tab =
  | 'policies'
  | 'connections'
  | 'snapshots'
  | 'jobs'
  | 'history'
  | 'restore-tests'
  | 'logs'
  | 'alerts'
  | 'updates';

interface UpdateSettingsForm {
  channel: AgentChannel;
  pinnedAgentVersion: string;
  pinnedResticVersion: string;
}

interface ConnectionForm {
  name: string;
  host: string;
  port: number;
  username: string;
  passwordSecret: string;
  binDirectory: string;
  /** Write-only, always starts empty (even when editing): left blank, the escrowed password is unchanged. */
  password: string;
}

interface CredentialsForm {
  accessKeyId: string;
  secretAccessKey: string;
  skipVerification: boolean;
}

function emptyConnectionForm(): ConnectionForm {
  return {
    name: '',
    host: 'localhost',
    port: 5432,
    username: 'postgres',
    passwordSecret: '',
    binDirectory: '',
    password: '',
  };
}

const REFRESH_MS = 15_000;

const SYSTEM_JOBS: Record<SystemJobType, { label: string; confirm: string }> = {
  RestartAgent: {
    label: 'Restart agent',
    confirm:
      'The agent service will restart as soon as it picks up the request (within 15 minutes).',
  },
  RestoreTest: {
    label: 'Run restore test',
    confirm:
      'Restores a sample of files and the latest database dumps into a temporary directory and verifies them.',
  },
  Retention: {
    label: 'Run retention',
    confirm:
      'Applies the retention rules of every policy (restic forget + prune). Old snapshots are removed.',
  },
  RepositoryCheck: {
    label: 'Check repository',
    confirm: 'Runs restic check reading a subset of the data.',
  },
};

@Component({
  selector: 'app-agent-detail',
  imports: [
    AlertsTable,
    Badge,
    BytesPipe,
    DateTimePipe,
    DurationPipe,
    FormsModule,
    ItemsTable,
    JobsTable,
    LogsTable,
    RelativeTimePipe,
    RouterLink,
    RunsTable,
    SnapshotBrowser,
  ],
  template: `
    @if (agent.value(); as a) {
      <div class="page-header">
        <div>
          <p class="muted"><a routerLink="/agents">Agents</a> /</p>
          <h1>
            {{ a.name }}
            @if (a.status === 'Active') {
              <app-badge [value]="a.online ? 'Online' : 'Offline'" />
            } @else {
              <app-badge [value]="a.status" />
            }
          </h1>
        </div>
        <div class="toolbar">
          @if (auth.canOperate()) {
            @if (a.status === 'Pending') {
              <button type="button" class="btn primary" (click)="generateToken(a)">
                Generate enrollment token
              </button>
            }
            @if (a.status === 'Disabled') {
              <button type="button" class="btn primary" (click)="enable(a)">Re-enable</button>
            }
            @if ((a.status === 'Pending' || a.status === 'Disabled') && auth.isOwner()) {
              <button type="button" class="btn danger" (click)="openDeleteForm(a)">
                Delete agent
              </button>
            }
            @if (a.status === 'Active') {
              <button type="button" class="btn" (click)="runSystemJob(a, 'RestoreTest')">
                Run restore test
              </button>
              <button type="button" class="btn" (click)="runSystemJob(a, 'RepositoryCheck')">
                Check repository
              </button>
              <button type="button" class="btn" (click)="runSystemJob(a, 'Retention')">
                Run retention
              </button>
              <button type="button" class="btn" (click)="runSystemJob(a, 'RestartAgent')">
                Restart agent
              </button>
              <button type="button" class="btn danger" (click)="disable(a)">Disable</button>
            }
          }
        </div>
      </div>

      @if (token(); as t) {
        <section class="card">
          <h2>Enrollment token</h2>
          <div class="secret-box">
            <span
              >Shown only once. Valid until {{ t.expiresAt | datetime }}, single use. Run on the VM
              as Administrator:</span
            >
            <code>{{ installCommand(t) }}</code>
            <div class="toolbar">
              <button type="button" class="btn small" (click)="copy(installCommand(t))">
                Copy command
              </button>
              <button type="button" class="btn small" (click)="copy(t.token)">Copy token</button>
              <button type="button" class="btn small" (click)="token.set(null)">Done</button>
            </div>
          </div>
        </section>
      }

      @if (showDeleteForm()) {
        <section class="card">
          <h2>Delete agent</h2>
          <p class="hint">
            Removes {{ a.name }} and all its policies, jobs, history, logs, alerts, connections and
            notifications. The repository in the bucket is <strong>not</strong> deleted from the
            storage provider.
          </p>
          <form (ngSubmit)="deleteAgent(a)" #deleteForm="ngForm">
            <label class="field">
              Type "{{ a.name }}" to confirm
              <input name="deleteConfirmName" [(ngModel)]="deleteConfirmName" required />
            </label>
            @if (deleteError()) {
              <div class="error-box">{{ deleteError() }}</div>
            }
            <div class="toolbar">
              <button
                type="submit"
                class="btn danger"
                [disabled]="deleteConfirmName !== a.name || deletingAgent()"
              >
                Delete permanently
              </button>
              <button type="button" class="btn" (click)="showDeleteForm.set(false)">Cancel</button>
            </div>
          </form>
        </section>
      }

      <section class="card">
        <dl class="facts">
          <div>
            <dt>Hostname</dt>
            <dd>{{ a.hostname ?? '—' }}</dd>
          </div>
          <div>
            <dt>OS</dt>
            <dd>{{ a.osVersion ?? '—' }}</dd>
          </div>
          <div>
            <dt>Agent version</dt>
            <dd class="mono">{{ a.version ?? '—' }}</dd>
          </div>
          <div>
            <dt>restic version</dt>
            <dd class="mono">{{ a.resticVersion ?? '—' }}</dd>
          </div>
          <div>
            <dt>Last heartbeat</dt>
            <dd [title]="a.lastHeartbeatAt | datetime">{{ a.lastHeartbeatAt | relative }}</dd>
          </div>
          <div>
            <dt>Last backup</dt>
            <dd [title]="a.lastBackupAt | datetime">{{ a.lastBackupAt | relative }}</dd>
          </div>
          <div>
            <dt>Free disk space</dt>
            <dd>{{ a.freeDiskSpace | bytes }}</dd>
          </div>
          <div>
            <dt>Enrolled</dt>
            <dd>{{ a.enrolledAt | datetime }}</dd>
          </div>
          <div>
            <dt>Created</dt>
            <dd>{{ a.createdAt | datetime }}</dd>
          </div>
        </dl>
      </section>

      <section class="card">
        <div class="card-header">
          <h2>Repository</h2>
          @if (auth.isOwner()) {
            <button type="button" class="btn small" (click)="openCredentialsForm(a)">
              Update credentials
            </button>
          }
        </div>
        <dl class="facts">
          <div>
            <dt>Endpoint / bucket</dt>
            <dd class="mono">{{ storageLabel() }}</dd>
          </div>
          <div>
            <dt>Prefix</dt>
            <dd class="mono">{{ a.storagePrefix }}</dd>
          </div>
          <div>
            <dt>Access key ID</dt>
            <dd class="mono">{{ a.s3AccessKeyId }}</dd>
          </div>
          <div>
            <dt>Credentials status</dt>
            <dd>
              <app-badge
                [value]="storageCredentialsStatus()"
                [text]="storageCredentialsStatusText()"
              />
              @if (a.s3CredentialsUpdatedAt) {
                <span class="muted"> — updated {{ a.s3CredentialsUpdatedAt | datetime }}</span>
              }
            </dd>
          </div>
        </dl>

        @if (showCredentialsForm()) {
          <form (ngSubmit)="saveStorageCredentials(a)" #credForm="ngForm">
            <div class="form-row">
              <label class="field">
                Access key ID
                <input name="credAccessKeyId" [(ngModel)]="credentialsForm.accessKeyId" required />
              </label>
              <label class="field">
                Secret access key
                <input
                  type="password"
                  name="credSecretAccessKey"
                  [(ngModel)]="credentialsForm.secretAccessKey"
                  autocomplete="new-password"
                  required
                />
              </label>
            </div>
            <label class="check">
              <input
                type="checkbox"
                name="credSkipVerification"
                [(ngModel)]="credentialsForm.skipVerification"
              />
              Skip verification (do not try to list the repository with the new key first)
            </label>
            <p class="hint">
              Revoke the old key with the storage provider only once the status above reads
              "Applied by the agent".
            </p>
            @if (credentialsError()) {
              <div class="error-box">{{ credentialsError() }}</div>
            }
            <div class="toolbar">
              <button
                type="submit"
                class="btn primary"
                [disabled]="credForm.invalid || savingCredentials()"
              >
                Save
              </button>
              <button type="button" class="btn" (click)="cancelCredentialsForm()">Cancel</button>
            </div>
          </form>
        }
      </section>

      <div class="tabs" role="tablist">
        <button type="button" [class.active]="tab() === 'policies'" (click)="tab.set('policies')">
          Policies<span class="count">{{ policies.value()?.length ?? 0 }}</span>
        </button>
        <button
          type="button"
          [class.active]="tab() === 'connections'"
          (click)="tab.set('connections')"
        >
          Connections<span class="count">{{ connections.value()?.length ?? 0 }}</span>
        </button>
        <button type="button" [class.active]="tab() === 'snapshots'" (click)="tab.set('snapshots')">
          Snapshots
        </button>
        <button type="button" [class.active]="tab() === 'jobs'" (click)="tab.set('jobs')">
          Jobs
        </button>
        <button type="button" [class.active]="tab() === 'history'" (click)="tab.set('history')">
          Backup history
        </button>
        <button
          type="button"
          [class.active]="tab() === 'restore-tests'"
          (click)="tab.set('restore-tests')"
        >
          Restore tests
        </button>
        <button type="button" [class.active]="tab() === 'logs'" (click)="tab.set('logs')">
          Logs
        </button>
        <button type="button" [class.active]="tab() === 'alerts'" (click)="tab.set('alerts')">
          Alerts<span class="count">{{ openAlertCount() }}</span>
        </button>
        <button type="button" [class.active]="tab() === 'updates'" (click)="tab.set('updates')">
          Updates
        </button>
      </div>

      @switch (tab()) {
        @case ('policies') {
          <section class="card flush">
            <div class="card-header">
              <h2>Backup policies</h2>
              @if (auth.canOperate()) {
                <div class="toolbar">
                  <button
                    type="button"
                    class="btn small"
                    [disabled]="a.status !== 'Active' || runningAll()"
                    (click)="runAllPolicies(a)"
                  >
                    Run all
                  </button>
                  <a class="btn primary small" [routerLink]="['/agents', a.id, 'policies', 'new']"
                    >New policy</a
                  >
                </div>
              }
            </div>
            @if ((policies.value() ?? []).length === 0) {
              <div class="empty">No policies. Nothing is backed up for this agent yet.</div>
            } @else {
              <div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Name</th>
                      <th>Sources</th>
                      <th>Schedule</th>
                      <th>Next run</th>
                      <th>Retention</th>
                      <th>Enabled</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (p of policies.value() ?? []; track p.id) {
                      <tr>
                        <td>
                          <a [routerLink]="['/policies', p.id]">{{ p.name }}</a>
                        </td>
                        <td>
                          @for (s of p.sources; track s.sourceId) {
                            <div>
                              <span class="badge tone-muted">{{
                                s.type === 'directory' ? 'dir' : 'pg'
                              }}</span>
                              {{
                                s.type === 'directory'
                                  ? s.paths.join(', ')
                                  : (connectionNames()[s.connectionId] ?? 'unknown connection')
                              }}
                            </div>
                          }
                        </td>
                        <td class="mono nowrap">
                          {{ p.cron }} <span class="muted">{{ p.timeZone }}</span>
                        </td>
                        <td class="nowrap">{{ p.nextRunAt | datetime }}</td>
                        <td class="nowrap">
                          {{ p.retention.keepDaily }}d / {{ p.retention.keepWeekly }}w /
                          {{ p.retention.keepMonthly }}m
                        </td>
                        <td>
                          <app-badge
                            [value]="p.enabled ? 'Active' : 'Disabled'"
                            [text]="p.enabled ? 'Yes' : 'No'"
                          />
                        </td>
                        <td class="num">
                          @if (auth.canOperate()) {
                            <button
                              type="button"
                              class="btn small"
                              [disabled]="a.status !== 'Active'"
                              (click)="runPolicy(p)"
                            >
                              Run now
                            </button>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
        }
        @case ('connections') {
          @if (auth.canOperate()) {
            <section class="card form">
              <h2>{{ editingConnectionId() ? 'Edit connection' : 'New connection' }}</h2>
              <form (ngSubmit)="saveConnection(a.id)" #connForm="ngForm">
                <div class="form-row">
                  <label class="field"
                    >Name <input name="connName" [(ngModel)]="connectionForm.name" required
                  /></label>
                  <label class="field"
                    >Host <input name="connHost" [(ngModel)]="connectionForm.host" required
                  /></label>
                  <label class="field"
                    >Port
                    <input type="number" name="connPort" [(ngModel)]="connectionForm.port" required
                  /></label>
                </div>
                <div class="form-row">
                  <label class="field"
                    >Username <input name="connUser" [(ngModel)]="connectionForm.username" required
                  /></label>
                  <label class="field">
                    Password secret name
                    <input name="connSecret" [(ngModel)]="connectionForm.passwordSecret" required />
                    <span class="hint">Key the password below is escrowed under on the server.</span>
                  </label>
                  <label class="field">
                    pg_dump directory
                    <span class="hint">Optional. Auto-detected from the registry when empty.</span>
                    <input
                      name="connBin"
                      [(ngModel)]="connectionForm.binDirectory"
                      placeholder="C:\\Program Files\\PostgreSQL\\18\\bin"
                    />
                  </label>
                </div>
                <div class="form-row">
                  <label class="field">
                    Password
                    @if (editingConnectionId()) {
                      <app-badge
                        [value]="editingConnectionPasswordSet() ? 'Active' : 'Disabled'"
                        [text]="editingConnectionPasswordSet() ? 'Set' : 'Missing'"
                      />
                    }
                    <input
                      type="password"
                      name="connPassword"
                      autocomplete="new-password"
                      [(ngModel)]="connectionForm.password"
                    />
                    <span class="hint">{{
                      editingConnectionId()
                        ? 'Write-only: leave empty to keep the current password.'
                        : 'Write-only: never shown again once saved.'
                    }}</span>
                  </label>
                </div>
                @if (connectionError()) {
                  <div class="error-box">{{ connectionError() }}</div>
                }
                <div class="toolbar">
                  <button
                    type="submit"
                    class="btn primary"
                    [disabled]="connForm.invalid || savingConnection()"
                  >
                    {{ editingConnectionId() ? 'Save changes' : 'Add connection' }}
                  </button>
                  @if (editingConnectionId()) {
                    <button type="button" class="btn" (click)="cancelEditConnection()">
                      Cancel
                    </button>
                  }
                </div>
              </form>
            </section>
          }

          <section class="card flush">
            <div class="card-header"><h2>Connections</h2></div>
            @if ((connections.value() ?? []).length === 0) {
              <div class="empty">
                No PostgreSQL connections. Add one, then reference it from a policy source.
              </div>
            } @else {
              <div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Name</th>
                      <th>Host</th>
                      <th>Port</th>
                      <th>Username</th>
                      <th>Password secret</th>
                      <th>Password</th>
                      <th>pg_dump dir</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (c of connections.value() ?? []; track c.id) {
                      <tr>
                        <td>{{ c.name }}</td>
                        <td class="mono">{{ c.host }}</td>
                        <td class="mono">{{ c.port }}</td>
                        <td class="mono">{{ c.username }}</td>
                        <td class="mono">{{ c.passwordSecret }}</td>
                        <td>
                          <app-badge
                            [value]="c.passwordSet ? 'Active' : 'Disabled'"
                            [text]="c.passwordSet ? 'Set' : 'Missing'"
                          />
                        </td>
                        <td class="mono">{{ c.binDirectory ?? '—' }}</td>
                        <td class="num">
                          @if (auth.canOperate()) {
                            <button type="button" class="btn small" (click)="editConnection(c)">
                              Edit
                            </button>
                            <button
                              type="button"
                              class="btn small danger"
                              (click)="deleteConnection(c)"
                            >
                              Delete
                            </button>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
        }
        @case ('snapshots') {
          <section class="card">
            <app-snapshot-browser [agentId]="a.id" (restored)="tab.set('jobs')" />
          </section>
        }
        @case ('jobs') {
          <section class="card flush">
            <app-jobs-table [jobs]="jobs.items()" (cancel)="cancelJob($event)" />
            @if (jobs.next()) {
              <div class="toolbar" style="padding: 1rem">
                <button
                  type="button"
                  class="btn"
                  [disabled]="jobs.loadingMore()"
                  (click)="jobs.loadMore()"
                >
                  {{ jobs.loadingMore() ? 'Loading…' : 'Load more' }}
                </button>
              </div>
            }
          </section>
        }
        @case ('history') {
          <section class="card flush">
            <app-runs-table [runs]="runs.items()" />
            @if (runs.next()) {
              <div class="toolbar" style="padding: 1rem">
                <button
                  type="button"
                  class="btn"
                  [disabled]="runs.loadingMore()"
                  (click)="runs.loadMore()"
                >
                  {{ runs.loadingMore() ? 'Loading…' : 'Load more' }}
                </button>
              </div>
            }
          </section>
        }
        @case ('restore-tests') {
          <section class="card flush">
            @if (restoreTests.items().length === 0) {
              <div class="empty">
                No restore test yet. They run weekly, or use "Run restore test".
              </div>
            } @else {
              <div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Scheduled</th>
                      <th>Trigger</th>
                      <th>Result</th>
                      <th>Duration</th>
                      <th>Checks</th>
                      <th>Error</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (t of restoreTests.items(); track t.id; let first = $first) {
                      <tr>
                        <td class="nowrap">{{ t.scheduledAt | datetime }}</td>
                        <td>{{ t.trigger }}</td>
                        <td><app-badge [value]="t.state" /></td>
                        <td class="nowrap">{{ t.startedAt | duration: t.completedAt }}</td>
                        <td class="nowrap">{{ passed(t) }} / {{ t.items.length }} passed</td>
                        <td class="muted">{{ t.error }}</td>
                        <td class="num">
                          @if (t.items.length > 0) {
                            <button type="button" class="link" (click)="toggleTest(t.id)">
                              {{ isTestExpanded(t.id, first) ? 'Hide' : 'Details' }}
                            </button>
                          }
                        </td>
                      </tr>
                      @if (isTestExpanded(t.id, first) && t.items.length > 0) {
                        <tr class="detail">
                          <td colspan="7"><app-items-table [items]="t.items" /></td>
                        </tr>
                      }
                    }
                  </tbody>
                </table>
              </div>
            }
            @if (restoreTests.next()) {
              <div class="toolbar" style="padding: 1rem">
                <button
                  type="button"
                  class="btn"
                  [disabled]="restoreTests.loadingMore()"
                  (click)="restoreTests.loadMore()"
                >
                  {{ restoreTests.loadingMore() ? 'Loading…' : 'Load more' }}
                </button>
              </div>
            }
          </section>
        }
        @case ('logs') {
          <section class="card flush">
            <div class="card-header">
              <h2>Logs</h2>
              <select
                [ngModel]="logLevel()"
                (ngModelChange)="logLevel.set($event)"
                style="width: auto"
              >
                <option value="">All levels</option>
                <option value="Information">Information</option>
                <option value="Warning">Warning</option>
                <option value="Error">Error</option>
              </select>
            </div>
            <app-logs-table [logs]="logs.items()" />
            @if (logs.next()) {
              <div class="toolbar" style="padding: 1rem">
                <button
                  type="button"
                  class="btn"
                  [disabled]="logs.loadingMore()"
                  (click)="logs.loadMore()"
                >
                  {{ logs.loadingMore() ? 'Loading…' : 'Load more' }}
                </button>
              </div>
            }
          </section>
        }
        @case ('alerts') {
          <section class="card flush">
            <app-alerts-table [alerts]="alerts.value() ?? []" />
          </section>
        }
        @case ('updates') {
          <section class="card">
            <dl class="facts">
              <div>
                <dt>Platform</dt>
                <dd class="mono">{{ a.platform }}</dd>
              </div>
              <div>
                <dt>Launcher managed</dt>
                <dd>
                  <app-badge
                    [value]="a.launcherManaged ? 'Active' : 'Disabled'"
                    [text]="a.launcherManaged ? 'Yes' : 'No'"
                  />
                </dd>
              </div>
              <div>
                <dt>Channel</dt>
                <dd class="mono">{{ a.channel }}</dd>
              </div>
              <div>
                <dt>Agent version (running / desired)</dt>
                <dd class="mono">{{ a.version ?? '—' }} / {{ a.desiredAgentVersion ?? '—' }}</dd>
              </div>
              <div>
                <dt>restic version (running / desired)</dt>
                <dd class="mono">
                  {{ a.resticVersion ?? '—' }} / {{ a.desiredResticVersion ?? '—' }}
                </dd>
              </div>
              <div>
                <dt>Last update</dt>
                <dd>
                  @if (a.lastUpdateVersion) {
                    <span class="mono">{{ a.lastUpdateVersion }}</span>
                    <app-badge [value]="a.lastUpdateOutcome" />
                    <span class="muted"> — {{ a.lastUpdateAt | datetime }}</span>
                  } @else {
                    <span class="muted">None</span>
                  }
                </dd>
              </div>
              @if (a.lastUpdateError) {
                <div>
                  <dt>Last update error</dt>
                  <dd class="error-box">{{ a.lastUpdateError }}</dd>
                </div>
              }
              @if (a.resticUpdateError) {
                <div>
                  <dt>restic update error</dt>
                  <dd class="error-box">{{ a.resticUpdateError }}</dd>
                </div>
              }
            </dl>
          </section>

          @if (auth.canOperate()) {
            <section class="card form">
              <h2>Update settings</h2>
              <form (ngSubmit)="saveSettings(a)">
                <div class="form-row">
                  <label class="field">
                    Channel
                    <select name="channel" [(ngModel)]="settings.channel">
                      @for (c of channels; track c) {
                        <option [value]="c">{{ c }}</option>
                      }
                    </select>
                  </label>
                  <label class="field">
                    Pinned agent version
                    <select name="pinnedAgentVersion" [(ngModel)]="settings.pinnedAgentVersion">
                      <option value="">none (follow channel)</option>
                      @for (r of agentReleasesForPlatform(); track r.id) {
                        <option [value]="r.version">{{ r.version }} ({{ r.channel }})</option>
                      }
                    </select>
                  </label>
                  <label class="field">
                    Pinned restic version
                    <select name="pinnedResticVersion" [(ngModel)]="settings.pinnedResticVersion">
                      <option value="">none (follow channel)</option>
                      @for (r of resticReleasesForPlatform(); track r.id) {
                        <option [value]="r.version">{{ r.version }}</option>
                      }
                    </select>
                  </label>
                </div>
                <div class="toolbar">
                  <button type="submit" class="btn primary" [disabled]="savingSettings()">
                    Save
                  </button>
                </div>
              </form>
            </section>
          }
        }
      }
    } @else if (agent.isLoading()) {
      <p class="muted">Loading…</p>
    } @else if (agent.error()) {
      <p class="error-box">Agent not found.</p>
    }
  `,
})
export class AgentDetailPage {
  readonly id = input.required<string>();

  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  protected readonly tab = signal<Tab>('policies');
  protected readonly logLevel = signal('');
  protected readonly token = signal<EnrollmentToken | null>(null);
  private readonly expandedTest = signal<string | null | undefined>(undefined);

  protected readonly agent = httpResource<Agent>(() => `/api/admin/agents/${this.id()}`);
  protected readonly storageTargets = httpResource<StorageTarget[]>(
    () => '/api/admin/storage-targets',
  );
  protected readonly policies = httpResource<Policy[]>(
    () => `/api/admin/agents/${this.id()}/policies`,
  );
  // Not gated on the 'connections' tab: also needed for the policies tab's Sources column and its tab badge.
  protected readonly connections = httpResource<PgConnection[]>(
    () => `/api/admin/agents/${this.id()}/connections`,
  );
  protected readonly jobs = new PagedList<Job>((before) =>
    this.api.jobs({ agentId: this.id(), before, limit: 100 }),
  );
  protected readonly runs = new PagedList<Run>((before) =>
    this.api.runs({ agentId: this.id(), before, limit: 100 }),
  );
  protected readonly restoreTests = new PagedList<Job>((before) =>
    this.api.jobs({ agentId: this.id(), type: 'RestoreTest', before, limit: 50 }),
  );
  protected readonly logs = new PagedList<LogEntry>((before) =>
    this.api.logs({ agentId: this.id(), level: this.logLevel() || undefined, before, limit: 300 }),
  );
  protected readonly alerts = httpResource<Alert[]>(() => ({
    url: '/api/admin/alerts',
    params: { agentId: this.id(), open: false, limit: 100 },
  }));
  protected readonly agentReleases = httpResource<Release[]>(() =>
    this.tab() === 'updates'
      ? { url: '/api/admin/releases', params: { product: 'agent' } }
      : undefined,
  );
  protected readonly resticReleases = httpResource<Release[]>(() =>
    this.tab() === 'updates'
      ? { url: '/api/admin/releases', params: { product: 'restic' } }
      : undefined,
  );

  protected readonly connectionNames = computed(() => lookup(this.connections.value()));
  protected readonly openAlertCount = computed(
    () => (this.alerts.value() ?? []).filter((a) => !a.resolvedAt).length,
  );
  protected readonly storageLabel = computed(() => {
    const agent = this.agent.value();
    const target = this.storageTargets.value()?.find((s) => s.id === agent?.storageTargetId);
    return target ? `${target.endpoint}/${target.bucket}` : '…';
  });
  protected readonly agentReleasesForPlatform = computed(() => {
    const platform = this.agent.value()?.platform;
    return (this.agentReleases.value() ?? []).filter((r) => r.platform === platform);
  });
  protected readonly resticReleasesForPlatform = computed(() => {
    const platform = this.agent.value()?.platform;
    return (this.resticReleases.value() ?? []).filter((r) => r.platform === platform);
  });

  protected readonly channels = AGENT_CHANNELS;
  protected settings: UpdateSettingsForm = {
    channel: 'stable',
    pinnedAgentVersion: '',
    pinnedResticVersion: '',
  };
  protected readonly savingSettings = signal(false);
  private readonly settingsReady = signal(false);

  protected connectionForm: ConnectionForm = emptyConnectionForm();
  protected readonly editingConnectionId = signal<string | null>(null);
  protected readonly editingConnectionPasswordSet = signal(false);
  protected readonly savingConnection = signal(false);
  protected readonly connectionError = signal<string | null>(null);

  protected credentialsForm: CredentialsForm = { accessKeyId: '', secretAccessKey: '', skipVerification: false };
  protected readonly showCredentialsForm = signal(false);
  protected readonly savingCredentials = signal(false);
  protected readonly credentialsError = signal<string | null>(null);

  protected readonly showDeleteForm = signal(false);
  protected readonly deletingAgent = signal(false);
  protected readonly deleteError = signal<string | null>(null);
  protected deleteConfirmName = '';

  protected readonly runningAll = signal(false);

  /** "Active" (applied), "Pending" (not yet applied) or "Missed" (agent too old to report it at all). */
  protected readonly storageCredentialsStatus = computed<'Active' | 'Pending' | 'Missed'>(() => {
    const a = this.agent.value();
    if (!a || a.s3CredentialsAppliedVersion === null) return a?.lastHeartbeatAt ? 'Missed' : 'Pending';
    return a.s3CredentialsAppliedVersion >= a.s3CredentialsVersion ? 'Active' : 'Pending';
  });
  protected readonly storageCredentialsStatusText = computed(() => {
    switch (this.storageCredentialsStatus()) {
      case 'Active':
        return 'Applied by the agent';
      case 'Missed':
        return 'Agent too old for automatic credential updates';
      default:
        return 'Waiting for the agent to apply them';
    }
  });

  constructor() {
    const timer = setInterval(() => this.refresh(), REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));

    effect(() => {
      if (this.settingsReady()) return;
      const a = this.agent.value();
      if (!a) return;
      this.settings = {
        channel: a.channel,
        pinnedAgentVersion: a.pinnedAgentVersion ?? '',
        pinnedResticVersion: a.pinnedResticVersion ?? '',
      };
      this.settingsReady.set(true);
    });

    // Paged lists (jobs/history/restore-tests/logs) fetch on demand, not reactively like httpResource: load
    // the active tab's first page when it becomes active, the agent changes, or (logs) the level filter does.
    effect(() => {
      const activeTab = this.tab();
      this.id(); // re-run on agent navigation too
      switch (activeTab) {
        case 'jobs':
          this.jobs.reload();
          break;
        case 'history':
          this.runs.reload();
          break;
        case 'restore-tests':
          this.restoreTests.reload();
          break;
        case 'logs':
          this.logLevel();
          this.logs.reload();
          break;
      }
    });
  }

  private refresh(): void {
    this.agent.reload();
    this.alerts.reload();
    switch (this.tab()) {
      case 'jobs':
        this.jobs.reload();
        break;
      case 'history':
        this.runs.reload();
        break;
      case 'restore-tests':
        this.restoreTests.reload();
        break;
      case 'logs':
        this.logs.reload();
        break;
      case 'policies':
        this.policies.reload();
        this.connections.reload();
        break;
      case 'connections':
        this.connections.reload();
        break;
      case 'updates':
        this.agentReleases.reload();
        this.resticReleases.reload();
        break;
    }
  }

  protected installCommand(t: EnrollmentToken): string {
    return `dupli-agent.exe install --server ${window.location.origin} --token ${t.token}`;
  }

  protected async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.toasts.success('Copied to clipboard');
    } catch {
      this.toasts.error('Clipboard not available: select the text and copy it manually');
    }
  }

  protected generateToken(a: Agent): void {
    this.api.createEnrollmentToken(a.id).subscribe((t) => this.token.set(t));
  }

  protected async runSystemJob(a: Agent, type: SystemJobType): Promise<void> {
    const job = SYSTEM_JOBS[type];
    if (!(await this.confirm.ask(`${job.label}?`, job.confirm, { confirmLabel: job.label })))
      return;
    this.api.runSystemJob(a.id, type).subscribe(() => {
      this.toasts.success(`${job.label}: queued`);
      this.jobs.reload();
      this.restoreTests.reload();
    });
  }

  protected async disable(a: Agent): Promise<void> {
    const ok = await this.confirm.ask(
      `Disable ${a.name}?`,
      'The agent can no longer authenticate and no job is scheduled for it. Re-enabling requires a new enrollment token.',
      { confirmLabel: 'Disable', danger: true },
    );
    if (!ok) return;
    this.api.disableAgent(a.id).subscribe(() => {
      this.toasts.success(`${a.name} disabled`);
      this.agent.reload();
    });
  }

  protected async enable(a: Agent): Promise<void> {
    const ok = await this.confirm.ask(
      `Re-enable ${a.name}?`,
      'The agent goes back to Pending: generate a new enrollment token and run the install command on the VM again.',
      { confirmLabel: 'Re-enable' },
    );
    if (!ok) return;
    this.api.enableAgent(a.id).subscribe(() => {
      this.toasts.success(`${a.name} is pending enrollment`);
      this.agent.reload();
    });
  }

  protected openDeleteForm(a: Agent): void {
    this.deleteConfirmName = '';
    this.deleteError.set(null);
    this.showDeleteForm.set(true);
  }

  protected deleteAgent(a: Agent): void {
    if (this.deleteConfirmName !== a.name) return;
    this.deletingAgent.set(true);
    this.deleteError.set(null);
    this.api.deleteAgent(a.id).subscribe({
      next: () => {
        this.toasts.success(`${a.name} deleted`);
        this.router.navigate(['/agents']);
      },
      error: (e) => {
        this.deleteError.set(problemMessage(e));
        this.deletingAgent.set(false);
      },
    });
  }

  protected runPolicy(p: Policy): void {
    this.api.runPolicy(p.id).subscribe(() => this.toasts.success(`Backup "${p.name}" queued`));
  }

  protected runAllPolicies(a: Agent): void {
    this.runningAll.set(true);
    this.api.runAllPolicies(a.id).subscribe({
      next: (jobs) => {
        this.runningAll.set(false);
        this.toasts.success(
          jobs.length > 0
            ? `${jobs.length} backup${jobs.length === 1 ? '' : 's'} queued`
            : 'Nothing to run: every policy already has a pending job',
        );
        this.jobs.reload();
      },
      error: () => this.runningAll.set(false),
    });
  }

  protected async cancelJob(job: Job): Promise<void> {
    if (
      !(await this.confirm.ask('Cancel job?', `${job.type} job will be cancelled.`, {
        confirmLabel: 'Cancel job',
        danger: true,
      }))
    )
      return;
    this.api.cancelJob(job.id).subscribe(() => this.jobs.reload());
  }

  protected passed(job: Job): number {
    return job.items.filter(
      (i) => i.outcome === 'Succeeded' || i.outcome === 'SucceededWithWarnings',
    ).length;
  }

  /** The most recent test starts expanded until the user toggles something. */
  protected isTestExpanded(id: string, first: boolean): boolean {
    const expanded = this.expandedTest();
    return expanded === undefined ? first : expanded === id;
  }

  protected toggleTest(id: string): void {
    const tests = this.restoreTests.items();
    const currentlyOpen = this.isTestExpanded(id, tests[0]?.id === id);
    this.expandedTest.set(currentlyOpen ? null : id);
  }

  protected saveSettings(a: Agent): void {
    this.savingSettings.set(true);
    const request: UpdateAgentSettingsRequest = {
      channel: this.settings.channel,
      pinnedAgentVersion: this.settings.pinnedAgentVersion || null,
      pinnedResticVersion: this.settings.pinnedResticVersion || null,
    };
    this.api
      .updateAgentSettings(a.id, request, new HttpContext().set(SILENT_ERRORS, true))
      .subscribe({
        next: () => {
          this.toasts.success('Update settings saved');
          this.savingSettings.set(false);
          this.settingsReady.set(false);
          this.agent.reload();
        },
        error: (e) => {
          this.toasts.error(problemMessage(e));
          this.savingSettings.set(false);
        },
      });
  }

  protected editConnection(c: PgConnection): void {
    this.editingConnectionId.set(c.id);
    this.editingConnectionPasswordSet.set(c.passwordSet);
    this.connectionError.set(null);
    this.connectionForm = {
      name: c.name,
      host: c.host,
      port: c.port,
      username: c.username,
      passwordSecret: c.passwordSecret,
      binDirectory: c.binDirectory ?? '',
      password: '',
    };
  }

  protected cancelEditConnection(): void {
    this.editingConnectionId.set(null);
    this.editingConnectionPasswordSet.set(false);
    this.connectionError.set(null);
    this.connectionForm = emptyConnectionForm();
  }

  protected saveConnection(agentId: string): void {
    const request: PgConnectionRequest = {
      name: this.connectionForm.name.trim(),
      host: this.connectionForm.host.trim(),
      port: Number(this.connectionForm.port),
      username: this.connectionForm.username.trim(),
      passwordSecret: this.connectionForm.passwordSecret.trim(),
      binDirectory: this.connectionForm.binDirectory.trim() || null,
      ...(this.connectionForm.password.trim() ? { password: this.connectionForm.password.trim() } : {}),
    };
    const context = new HttpContext().set(SILENT_ERRORS, true);
    const id = this.editingConnectionId();
    const call = id
      ? this.api.updateConnection(id, request, context)
      : this.api.createConnection(agentId, request, context);

    this.savingConnection.set(true);
    this.connectionError.set(null);
    call.subscribe({
      next: (connection) => {
        this.toasts.success(`Connection "${connection.name}" saved`);
        this.savingConnection.set(false);
        this.cancelEditConnection();
        this.connections.reload();
      },
      error: (e) => {
        this.connectionError.set(problemMessage(e));
        this.savingConnection.set(false);
      },
    });
  }

  protected async deleteConnection(c: PgConnection): Promise<void> {
    const ok = await this.confirm.ask(
      `Delete connection "${c.name}"?`,
      'Fails if a policy source still uses it for PostgreSQL backups.',
      { confirmLabel: 'Delete', danger: true },
    );
    if (!ok) return;
    this.api.deleteConnection(c.id).subscribe(() => {
      this.toasts.success('Connection deleted');
      this.connections.reload();
    });
  }

  protected openCredentialsForm(a: Agent): void {
    this.credentialsForm = { accessKeyId: a.s3AccessKeyId, secretAccessKey: '', skipVerification: false };
    this.credentialsError.set(null);
    this.showCredentialsForm.set(true);
  }

  protected cancelCredentialsForm(): void {
    this.showCredentialsForm.set(false);
    this.credentialsError.set(null);
  }

  protected saveStorageCredentials(a: Agent): void {
    const request: UpdateAgentStorageCredentialsRequest = {
      accessKeyId: this.credentialsForm.accessKeyId.trim(),
      secretAccessKey: this.credentialsForm.secretAccessKey,
      skipVerification: this.credentialsForm.skipVerification,
    };
    this.savingCredentials.set(true);
    this.credentialsError.set(null);
    this.api
      .updateAgentStorageCredentials(a.id, request, new HttpContext().set(SILENT_ERRORS, true))
      .subscribe({
        next: () => {
          this.toasts.success('Storage credentials updated');
          this.savingCredentials.set(false);
          this.showCredentialsForm.set(false);
          this.agent.reload();
        },
        error: (e) => {
          this.credentialsError.set(problemMessage(e));
          this.savingCredentials.set(false);
        },
      });
  }
}
