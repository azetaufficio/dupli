import { HttpContext, httpResource } from '@angular/common/http';
import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, params } from '../core/api.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import {
  Agent,
  AGENT_CHANNELS,
  AgentChannel,
  Alert,
  EnrollmentToken,
  Job,
  LogEntry,
  Policy,
  Release,
  Run,
  StorageTarget,
  SystemJobType,
  UpdateAgentSettingsRequest,
} from '../core/models';
import { ToastService } from '../core/toast.service';
import { Badge } from '../shared/badge';
import { ConfirmService } from '../shared/confirm';
import { BytesPipe, DateTimePipe, DurationPipe, RelativeTimePipe } from '../shared/format';
import { SnapshotBrowser } from '../shared/snapshot-browser';
import { AlertsTable, ItemsTable, JobsTable, LogsTable, RunsTable, lookup } from '../shared/tables';

type Tab =
  'policies' | 'snapshots' | 'jobs' | 'history' | 'restore-tests' | 'logs' | 'alerts' | 'updates';

interface UpdateSettingsForm {
  channel: AgentChannel;
  pinnedAgentVersion: string;
  pinnedResticVersion: string;
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
          @if (a.status === 'Pending') {
            <button type="button" class="btn primary" (click)="generateToken(a)">
              Generate enrollment token
            </button>
          }
          @if (a.status === 'Disabled') {
            <button type="button" class="btn primary" (click)="enable(a)">Re-enable</button>
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
            <dt>Repository</dt>
            <dd class="mono">{{ storageLabel() }}/{{ a.storagePrefix }}</dd>
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

      <div class="tabs" role="tablist">
        <button type="button" [class.active]="tab() === 'policies'" (click)="tab.set('policies')">
          Policies<span class="count">{{ policies.value()?.length ?? 0 }}</span>
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
              <a class="btn primary small" [routerLink]="['/agents', a.id, 'policies', 'new']"
                >New policy</a
              >
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
                                  : s.username + '@' + s.host + ':' + s.port
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
                          <button
                            type="button"
                            class="btn small"
                            [disabled]="a.status !== 'Active'"
                            (click)="runPolicy(p)"
                          >
                            Run now
                          </button>
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
            <app-jobs-table
              [jobs]="jobs.value() ?? []"
              [policies]="policyNames()"
              (cancel)="cancelJob($event)"
            />
          </section>
        }
        @case ('history') {
          <section class="card flush">
            <app-runs-table [runs]="runs.value() ?? []" [policies]="policyNames()" />
          </section>
        }
        @case ('restore-tests') {
          <section class="card flush">
            @if ((restoreTests.value() ?? []).length === 0) {
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
                    @for (t of restoreTests.value() ?? []; track t.id; let first = $first) {
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
            <app-logs-table [logs]="logs.value() ?? []" />
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
    } @else if (agent.isLoading()) {
      <p class="muted">Loading…</p>
    } @else if (agent.error()) {
      <p class="error-box">Agent not found.</p>
    }
  `,
})
export class AgentDetailPage {
  readonly id = input.required<string>();

  private readonly api = inject(ApiService);
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
  protected readonly jobs = httpResource<Job[]>(() =>
    this.tab() === 'jobs'
      ? { url: '/api/admin/jobs', params: { agentId: this.id(), limit: 100 } }
      : undefined,
  );
  protected readonly runs = httpResource<Run[]>(() =>
    this.tab() === 'history'
      ? { url: '/api/admin/runs', params: { agentId: this.id(), limit: 100 } }
      : undefined,
  );
  protected readonly restoreTests = httpResource<Job[]>(() =>
    this.tab() === 'restore-tests'
      ? { url: '/api/admin/jobs', params: { agentId: this.id(), type: 'RestoreTest', limit: 50 } }
      : undefined,
  );
  protected readonly logs = httpResource<LogEntry[]>(() =>
    this.tab() === 'logs'
      ? {
          url: '/api/admin/logs',
          params: params({ agentId: this.id(), level: this.logLevel(), limit: 300 }),
        }
      : undefined,
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

  protected readonly policyNames = computed(() => lookup(this.policies.value()));
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
      case 'policies':
        this.policies.reload();
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

  protected runPolicy(p: Policy): void {
    this.api.runPolicy(p.id).subscribe(() => this.toasts.success(`Backup "${p.name}" queued`));
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
    const tests = this.restoreTests.value() ?? [];
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
}
