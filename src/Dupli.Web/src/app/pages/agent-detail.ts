import { HttpContext, httpResource } from '@angular/common/http';
import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';
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
    binDirectory: '',
    password: '',
  };
}

const REFRESH_MS = 15_000;

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
    TranslocoModule,
  ],
  template: `
    @if (agent.value(); as a) {
      <div class="page-header">
        <div>
          <p class="muted"><a routerLink="/agents">{{ 'agentDetail.breadcrumb' | transloco }}</a> /</p>
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
                {{ 'agentDetail.actions.generateToken' | transloco }}
              </button>
            }
            @if (a.status === 'Disabled') {
              <button type="button" class="btn primary" (click)="enable(a)">
                {{ 'agentDetail.actions.reEnable' | transloco }}
              </button>
            }
            @if ((a.status === 'Pending' || a.status === 'Disabled') && auth.isOwner()) {
              <button type="button" class="btn danger" (click)="openDeleteForm(a)">
                {{ 'agentDetail.actions.deleteAgent' | transloco }}
              </button>
            }
            @if (a.status === 'Active') {
              <button type="button" class="btn" (click)="runSystemJob(a, 'RestoreTest')">
                {{ 'agentDetail.systemJobs.RestoreTest.label' | transloco }}
              </button>
              <button type="button" class="btn" (click)="runSystemJob(a, 'RepositoryCheck')">
                {{ 'agentDetail.systemJobs.RepositoryCheck.label' | transloco }}
              </button>
              <button type="button" class="btn" (click)="runSystemJob(a, 'Retention')">
                {{ 'agentDetail.systemJobs.Retention.label' | transloco }}
              </button>
              <button type="button" class="btn" (click)="runSystemJob(a, 'RestartAgent')">
                {{ 'agentDetail.systemJobs.RestartAgent.label' | transloco }}
              </button>
              <button type="button" class="btn danger" (click)="disable(a)">
                {{ 'agentDetail.actions.disable' | transloco }}
              </button>
            }
          }
        </div>
      </div>

      @if (token(); as t) {
        <section class="card">
          <h2>{{ 'agentDetail.enrollmentToken.title' | transloco }}</h2>
          <div class="secret-box">
            <span>{{
              'agentDetail.enrollmentToken.shownOnce'
                | transloco: { expires: t.expiresAt | datetime }
            }}</span>
            <code>{{ installCommand(t) }}</code>
            <div class="toolbar">
              <button type="button" class="btn small" (click)="copy(installCommand(t))">
                {{ 'agentDetail.enrollmentToken.copyCommand' | transloco }}
              </button>
              <button type="button" class="btn small" (click)="copy(t.token)">
                {{ 'agentDetail.enrollmentToken.copyToken' | transloco }}
              </button>
              <button type="button" class="btn small" (click)="token.set(null)">
                {{ 'agentDetail.enrollmentToken.done' | transloco }}
              </button>
            </div>
          </div>
        </section>
      }

      @if (showDeleteForm()) {
        <section class="card">
          <h2>{{ 'agentDetail.actions.deleteAgent' | transloco }}</h2>
          <p class="hint" [innerHTML]="'agentDetail.deleteForm.hint' | transloco: { name: a.name }"></p>
          <form (ngSubmit)="deleteAgent(a)" #deleteForm="ngForm">
            <label class="field">
              {{ 'agentDetail.deleteForm.confirmPrompt' | transloco: { name: a.name } }}
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
                {{ 'agentDetail.deleteForm.deletePermanently' | transloco }}
              </button>
              <button type="button" class="btn" (click)="showDeleteForm.set(false)">
                {{ 'common.cancel' | transloco }}
              </button>
            </div>
          </form>
        </section>
      }

      <section class="card">
        <dl class="facts">
          <div>
            <dt>{{ 'agentDetail.facts.hostname' | transloco }}</dt>
            <dd>{{ a.hostname ?? '—' }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.facts.os' | transloco }}</dt>
            <dd>{{ a.osVersion ?? '—' }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.facts.agentVersion' | transloco }}</dt>
            <dd class="mono">{{ a.version ?? '—' }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.facts.resticVersion' | transloco }}</dt>
            <dd class="mono">{{ a.resticVersion ?? '—' }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.facts.lastHeartbeat' | transloco }}</dt>
            <dd [title]="a.lastHeartbeatAt | datetime">{{ a.lastHeartbeatAt | relative }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.facts.lastBackup' | transloco }}</dt>
            <dd [title]="a.lastBackupAt | datetime">{{ a.lastBackupAt | relative }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.facts.freeDiskSpace' | transloco }}</dt>
            <dd>{{ a.freeDiskSpace | bytes }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.facts.enrolled' | transloco }}</dt>
            <dd>{{ a.enrolledAt | datetime }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.facts.created' | transloco }}</dt>
            <dd>{{ a.createdAt | datetime }}</dd>
          </div>
        </dl>
      </section>

      <section class="card">
        <div class="card-header">
          <h2>{{ 'agentDetail.repository.title' | transloco }}</h2>
          @if (auth.isOwner()) {
            <button type="button" class="btn small" (click)="openCredentialsForm(a)">
              {{ 'agentDetail.repository.updateCredentials' | transloco }}
            </button>
          }
        </div>
        <dl class="facts">
          <div>
            <dt>{{ 'agentDetail.repository.endpointBucket' | transloco }}</dt>
            <dd class="mono">{{ storageLabel() }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.repository.prefix' | transloco }}</dt>
            <dd class="mono">{{ a.storagePrefix }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.repository.accessKeyId' | transloco }}</dt>
            <dd class="mono">{{ a.s3AccessKeyId }}</dd>
          </div>
          <div>
            <dt>{{ 'agentDetail.repository.credentialsStatus' | transloco }}</dt>
            <dd>
              <app-badge
                [value]="storageCredentialsStatus()"
                [text]="storageCredentialsStatusText()"
              />
              @if (a.s3CredentialsUpdatedAt) {
                <span class="muted">
                  {{
                    'agentDetail.repository.updatedAt'
                      | transloco: { date: a.s3CredentialsUpdatedAt | datetime }
                  }}
                </span>
              }
            </dd>
          </div>
        </dl>

        @if (showCredentialsForm()) {
          <form (ngSubmit)="saveStorageCredentials(a)" #credForm="ngForm">
            <div class="form-row">
              <label class="field">
                {{ 'agentDetail.repository.accessKeyId' | transloco }}
                <input name="credAccessKeyId" [(ngModel)]="credentialsForm.accessKeyId" required />
              </label>
              <label class="field">
                {{ 'agentDetail.credentialsForm.secretAccessKey' | transloco }}
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
              {{ 'agentDetail.credentialsForm.skipVerification' | transloco }}
            </label>
            <p
              class="hint"
              [innerHTML]="
                'agentDetail.credentialsForm.revokeHint'
                  | transloco: { status: 'agentDetail.status.applied' | transloco }
              "
            ></p>
            @if (credentialsError()) {
              <div class="error-box">{{ credentialsError() }}</div>
            }
            <div class="toolbar">
              <button
                type="submit"
                class="btn primary"
                [disabled]="credForm.invalid || savingCredentials()"
              >
                {{ 'common.save' | transloco }}
              </button>
              <button type="button" class="btn" (click)="cancelCredentialsForm()">
                {{ 'common.cancel' | transloco }}
              </button>
            </div>
          </form>
        }
      </section>

      <div class="tabs" role="tablist">
        <button type="button" [class.active]="tab() === 'policies'" (click)="tab.set('policies')">
          {{ 'agentDetail.tabs.policies' | transloco }}<span class="count">{{
            policies.value()?.length ?? 0
          }}</span>
        </button>
        <button
          type="button"
          [class.active]="tab() === 'connections'"
          (click)="tab.set('connections')"
        >
          {{ 'agentDetail.tabs.connections' | transloco }}<span class="count">{{
            connections.value()?.length ?? 0
          }}</span>
        </button>
        <button type="button" [class.active]="tab() === 'snapshots'" (click)="tab.set('snapshots')">
          {{ 'agentDetail.tabs.snapshots' | transloco }}
        </button>
        <button type="button" [class.active]="tab() === 'jobs'" (click)="tab.set('jobs')">
          {{ 'agentDetail.tabs.jobs' | transloco }}
        </button>
        <button type="button" [class.active]="tab() === 'history'" (click)="tab.set('history')">
          {{ 'agentDetail.tabs.history' | transloco }}
        </button>
        <button
          type="button"
          [class.active]="tab() === 'restore-tests'"
          (click)="tab.set('restore-tests')"
        >
          {{ 'agentDetail.tabs.restoreTests' | transloco }}
        </button>
        <button type="button" [class.active]="tab() === 'logs'" (click)="tab.set('logs')">
          {{ 'agentDetail.tabs.logs' | transloco }}
        </button>
        <button type="button" [class.active]="tab() === 'alerts'" (click)="tab.set('alerts')">
          {{ 'agentDetail.tabs.alerts' | transloco }}<span class="count">{{ openAlertCount() }}</span>
        </button>
        <button type="button" [class.active]="tab() === 'updates'" (click)="tab.set('updates')">
          {{ 'agentDetail.tabs.updates' | transloco }}
        </button>
      </div>

      @switch (tab()) {
        @case ('policies') {
          <section class="card flush">
            <div class="card-header">
              <h2>{{ 'agentDetail.policiesTab.title' | transloco }}</h2>
              @if (auth.canOperate()) {
                <div class="toolbar">
                  <button
                    type="button"
                    class="btn small"
                    [disabled]="a.status !== 'Active' || runningAll()"
                    (click)="runAllPolicies(a)"
                  >
                    {{ 'agentDetail.policiesTab.runAll' | transloco }}
                  </button>
                  <a class="btn primary small" [routerLink]="['/agents', a.id, 'policies', 'new']">{{
                    'agentDetail.policiesTab.newPolicy' | transloco
                  }}</a>
                </div>
              }
            </div>
            @if ((policies.value() ?? []).length === 0) {
              <div class="empty">{{ 'agentDetail.policiesTab.empty' | transloco }}</div>
            } @else {
              <div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>{{ 'agentDetail.policiesTab.table.name' | transloco }}</th>
                      <th>{{ 'agentDetail.policiesTab.table.sources' | transloco }}</th>
                      <th>{{ 'agentDetail.policiesTab.table.schedule' | transloco }}</th>
                      <th>{{ 'agentDetail.policiesTab.table.nextRun' | transloco }}</th>
                      <th>{{ 'agentDetail.policiesTab.table.retention' | transloco }}</th>
                      <th>{{ 'agentDetail.policiesTab.table.enabled' | transloco }}</th>
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
                                s.type === 'directory'
                                  ? ('agentDetail.policiesTab.sourceType.dir' | transloco)
                                  : ('agentDetail.policiesTab.sourceType.pg' | transloco)
                              }}</span>
                              {{
                                s.type === 'directory'
                                  ? s.paths.join(', ')
                                  : (connectionNames()[s.connectionId] ??
                                    ('agentDetail.policiesTab.unknownConnection' | transloco))
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
                            [text]="(p.enabled ? 'common.yes' : 'common.no') | transloco"
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
                              {{ 'agentDetail.policiesTab.runNow' | transloco }}
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
              <h2>{{
                (editingConnectionId()
                  ? 'agentDetail.connectionsTab.editTitle'
                  : 'agentDetail.connectionsTab.newTitle'
                ) | transloco
              }}</h2>
              <form (ngSubmit)="saveConnection(a.id)" #connForm="ngForm">
                <div class="form-row">
                  <label class="field"
                    >{{ 'agentDetail.connectionsTab.table.name' | transloco }}
                    <input name="connName" [(ngModel)]="connectionForm.name" required
                  /></label>
                  <label class="field"
                    >{{ 'agentDetail.connectionsTab.table.host' | transloco }}
                    <input name="connHost" [(ngModel)]="connectionForm.host" required
                  /></label>
                  <label class="field"
                    >{{ 'agentDetail.connectionsTab.table.port' | transloco }}
                    <input type="number" name="connPort" [(ngModel)]="connectionForm.port" required
                  /></label>
                </div>
                <div class="form-row">
                  <label class="field"
                    >{{ 'agentDetail.connectionsTab.table.username' | transloco }}
                    <input name="connUser" [(ngModel)]="connectionForm.username" required
                  /></label>
                  <label class="field">
                    {{ 'agentDetail.connectionsTab.binDirectory' | transloco }}
                    <span class="hint">{{
                      'agentDetail.connectionsTab.binDirectoryHint' | transloco
                    }}</span>
                    <input
                      name="connBin"
                      [(ngModel)]="connectionForm.binDirectory"
                      placeholder="C:\\Program Files\\PostgreSQL\\18\\bin"
                    />
                  </label>
                </div>
                <div class="form-row">
                  <label class="field">
                    {{ 'agentDetail.connectionsTab.table.password' | transloco }}
                    @if (editingConnectionId()) {
                      <app-badge
                        [value]="editingConnectionPasswordSet() ? 'Active' : 'Disabled'"
                        [text]="
                          (editingConnectionPasswordSet()
                            ? 'agentDetail.connectionsTab.passwordSet'
                            : 'agentDetail.connectionsTab.passwordMissing'
                          ) | transloco
                        "
                      />
                    }
                    <input
                      type="password"
                      name="connPassword"
                      autocomplete="new-password"
                      [(ngModel)]="connectionForm.password"
                    />
                    <span class="hint">{{
                      (editingConnectionId()
                        ? 'agentDetail.connectionsTab.passwordHintEditing'
                        : 'agentDetail.connectionsTab.passwordHintNew'
                      ) | transloco
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
                    {{
                      (editingConnectionId()
                        ? 'agentDetail.connectionsTab.saveChanges'
                        : 'agentDetail.connectionsTab.addConnection'
                      ) | transloco
                    }}
                  </button>
                  @if (editingConnectionId()) {
                    <button type="button" class="btn" (click)="cancelEditConnection()">
                      {{ 'common.cancel' | transloco }}
                    </button>
                  }
                </div>
              </form>
            </section>
          }

          <section class="card flush">
            <div class="card-header"><h2>{{ 'agentDetail.tabs.connections' | transloco }}</h2></div>
            @if ((connections.value() ?? []).length === 0) {
              <div class="empty">{{ 'agentDetail.connectionsTab.empty' | transloco }}</div>
            } @else {
              <div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>{{ 'agentDetail.connectionsTab.table.name' | transloco }}</th>
                      <th>{{ 'agentDetail.connectionsTab.table.host' | transloco }}</th>
                      <th>{{ 'agentDetail.connectionsTab.table.port' | transloco }}</th>
                      <th>{{ 'agentDetail.connectionsTab.table.username' | transloco }}</th>
                      <th>{{ 'agentDetail.connectionsTab.table.password' | transloco }}</th>
                      <th>{{ 'agentDetail.connectionsTab.table.pgDumpDir' | transloco }}</th>
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
                        <td>
                          <app-badge
                            [value]="c.passwordSet ? 'Active' : 'Disabled'"
                            [text]="
                              (c.passwordSet
                                ? 'agentDetail.connectionsTab.passwordSet'
                                : 'agentDetail.connectionsTab.passwordMissing'
                              ) | transloco
                            "
                          />
                        </td>
                        <td class="mono">{{ c.binDirectory ?? '—' }}</td>
                        <td class="num">
                          @if (auth.canOperate()) {
                            <button type="button" class="btn small" (click)="editConnection(c)">
                              {{ 'common.edit' | transloco }}
                            </button>
                            <button
                              type="button"
                              class="btn small danger"
                              (click)="deleteConnection(c)"
                            >
                              {{ 'common.delete' | transloco }}
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
                  {{ (jobs.loadingMore() ? 'common.loading' : 'agentDetail.loadMore') | transloco }}
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
                  {{ (runs.loadingMore() ? 'common.loading' : 'agentDetail.loadMore') | transloco }}
                </button>
              </div>
            }
          </section>
        }
        @case ('restore-tests') {
          <section class="card flush">
            @if (restoreTests.items().length === 0) {
              <div class="empty">{{ 'agentDetail.restoreTestsTab.empty' | transloco }}</div>
            } @else {
              <div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>{{ 'agentDetail.restoreTestsTab.table.scheduled' | transloco }}</th>
                      <th>{{ 'agentDetail.restoreTestsTab.table.trigger' | transloco }}</th>
                      <th>{{ 'agentDetail.restoreTestsTab.table.result' | transloco }}</th>
                      <th>{{ 'agentDetail.restoreTestsTab.table.duration' | transloco }}</th>
                      <th>{{ 'agentDetail.restoreTestsTab.table.checks' | transloco }}</th>
                      <th>{{ 'agentDetail.restoreTestsTab.table.error' | transloco }}</th>
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
                        <td class="nowrap">{{
                          'agentDetail.restoreTestsTab.passed'
                            | transloco: { passed: passed(t), total: t.items.length }
                        }}</td>
                        <td class="muted">{{ t.error }}</td>
                        <td class="num">
                          @if (t.items.length > 0) {
                            <button type="button" class="link" (click)="toggleTest(t.id)">
                              {{
                                (isTestExpanded(t.id, first)
                                  ? 'agentDetail.restoreTestsTab.hide'
                                  : 'agentDetail.restoreTestsTab.details'
                                ) | transloco
                              }}
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
                  {{
                    (restoreTests.loadingMore() ? 'common.loading' : 'agentDetail.loadMore')
                      | transloco
                  }}
                </button>
              </div>
            }
          </section>
        }
        @case ('logs') {
          <section class="card flush">
            <div class="card-header">
              <h2>{{ 'agentDetail.tabs.logs' | transloco }}</h2>
              <select
                [ngModel]="logLevel()"
                (ngModelChange)="logLevel.set($event)"
                style="width: auto"
              >
                <option value="">{{ 'logs.allLevels' | transloco }}</option>
                <option value="Information">{{ 'logs.levels.information' | transloco }}</option>
                <option value="Warning">{{ 'logs.levels.warning' | transloco }}</option>
                <option value="Error">{{ 'logs.levels.error' | transloco }}</option>
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
                  {{ (logs.loadingMore() ? 'common.loading' : 'agentDetail.loadMore') | transloco }}
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
                <dt>{{ 'agentDetail.updatesTab.platform' | transloco }}</dt>
                <dd class="mono">{{ a.platform }}</dd>
              </div>
              <div>
                <dt>{{ 'agentDetail.updatesTab.launcherManaged' | transloco }}</dt>
                <dd>
                  <app-badge
                    [value]="a.launcherManaged ? 'Active' : 'Disabled'"
                    [text]="(a.launcherManaged ? 'common.yes' : 'common.no') | transloco"
                  />
                </dd>
              </div>
              <div>
                <dt>{{ 'agentDetail.updatesTab.channel' | transloco }}</dt>
                <dd class="mono">{{ a.channel }}</dd>
              </div>
              <div>
                <dt>{{ 'agentDetail.updatesTab.agentVersionRunningDesired' | transloco }}</dt>
                <dd class="mono">{{ a.version ?? '—' }} / {{ a.desiredAgentVersion ?? '—' }}</dd>
              </div>
              <div>
                <dt>{{ 'agentDetail.updatesTab.resticVersionRunningDesired' | transloco }}</dt>
                <dd class="mono">
                  {{ a.resticVersion ?? '—' }} / {{ a.desiredResticVersion ?? '—' }}
                </dd>
              </div>
              <div>
                <dt>{{ 'agentDetail.updatesTab.lastUpdate' | transloco }}</dt>
                <dd>
                  @if (a.lastUpdateVersion) {
                    <span class="mono">{{ a.lastUpdateVersion }}</span>
                    <app-badge [value]="a.lastUpdateOutcome" />
                    <span class="muted"> — {{ a.lastUpdateAt | datetime }}</span>
                  } @else {
                    <span class="muted">{{ 'agentDetail.updatesTab.none' | transloco }}</span>
                  }
                </dd>
              </div>
              @if (a.lastUpdateError) {
                <div>
                  <dt>{{ 'agentDetail.updatesTab.lastUpdateError' | transloco }}</dt>
                  <dd class="error-box">{{ a.lastUpdateError }}</dd>
                </div>
              }
              @if (a.resticUpdateError) {
                <div>
                  <dt>{{ 'agentDetail.updatesTab.resticUpdateError' | transloco }}</dt>
                  <dd class="error-box">{{ a.resticUpdateError }}</dd>
                </div>
              }
            </dl>
          </section>

          @if (auth.canOperate()) {
            <section class="card form">
              <h2>{{ 'agentDetail.updatesTab.updateSettingsTitle' | transloco }}</h2>
              <form (ngSubmit)="saveSettings(a)">
                <div class="form-row">
                  <label class="field">
                    {{ 'agentDetail.updatesTab.channel' | transloco }}
                    <select name="channel" [(ngModel)]="settings.channel">
                      @for (c of channels; track c) {
                        <option [value]="c">{{ c }}</option>
                      }
                    </select>
                  </label>
                  <label class="field">
                    {{ 'agentDetail.updatesTab.pinnedAgentVersion' | transloco }}
                    <select name="pinnedAgentVersion" [(ngModel)]="settings.pinnedAgentVersion">
                      <option value="">{{ 'agentDetail.updatesTab.noneFollowChannel' | transloco }}</option>
                      @for (r of agentReleasesForPlatform(); track r.id) {
                        <option [value]="r.version">{{ r.version }} ({{ r.channel }})</option>
                      }
                    </select>
                  </label>
                  <label class="field">
                    {{ 'agentDetail.updatesTab.pinnedResticVersion' | transloco }}
                    <select name="pinnedResticVersion" [(ngModel)]="settings.pinnedResticVersion">
                      <option value="">{{ 'agentDetail.updatesTab.noneFollowChannel' | transloco }}</option>
                      @for (r of resticReleasesForPlatform(); track r.id) {
                        <option [value]="r.version">{{ r.version }}</option>
                      }
                    </select>
                  </label>
                </div>
                <div class="toolbar">
                  <button type="submit" class="btn primary" [disabled]="savingSettings()">
                    {{ 'common.save' | transloco }}
                  </button>
                </div>
              </form>
            </section>
          }
        }
      }
    } @else if (agent.isLoading()) {
      <p class="muted">{{ 'common.loading' | transloco }}</p>
    } @else if (agent.error()) {
      <p class="error-box">{{ 'agentDetail.notFound' | transloco }}</p>
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
  private readonly transloco = inject(TranslocoService);

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
        return this.transloco.translate('agentDetail.status.applied');
      case 'Missed':
        return this.transloco.translate('agentDetail.status.tooOldForCredentials');
      default:
        return this.transloco.translate('agentDetail.status.waitingForAgent');
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
      this.toasts.success(this.transloco.translate('agentDetail.toasts.copiedToClipboard'));
    } catch {
      this.toasts.error(this.transloco.translate('agentDetail.toasts.clipboardUnavailable'));
    }
  }

  protected generateToken(a: Agent): void {
    this.api.createEnrollmentToken(a.id).subscribe((t) => this.token.set(t));
  }

  protected async runSystemJob(a: Agent, type: SystemJobType): Promise<void> {
    const label = this.transloco.translate(`agentDetail.systemJobs.${type}.label`);
    const message = this.transloco.translate(`agentDetail.systemJobs.${type}.confirm`);
    if (!(await this.confirm.ask(`${label}?`, message, { confirmLabel: label }))) return;
    this.api.runSystemJob(a.id, type).subscribe(() => {
      this.toasts.success(this.transloco.translate('agentDetail.toasts.jobQueued', { label }));
      this.jobs.reload();
      this.restoreTests.reload();
    });
  }

  protected async disable(a: Agent): Promise<void> {
    const ok = await this.confirm.ask(
      this.transloco.translate('agentDetail.confirms.disableAgentTitle', { name: a.name }),
      this.transloco.translate('agentDetail.confirms.disableAgentBody'),
      { confirmLabel: this.transloco.translate('agentDetail.actions.disable'), danger: true },
    );
    if (!ok) return;
    this.api.disableAgent(a.id).subscribe(() => {
      this.toasts.success(this.transloco.translate('agentDetail.toasts.agentDisabled', { name: a.name }));
      this.agent.reload();
    });
  }

  protected async enable(a: Agent): Promise<void> {
    const ok = await this.confirm.ask(
      this.transloco.translate('agentDetail.confirms.enableAgentTitle', { name: a.name }),
      this.transloco.translate('agentDetail.confirms.enableAgentBody'),
      { confirmLabel: this.transloco.translate('agentDetail.actions.reEnable') },
    );
    if (!ok) return;
    this.api.enableAgent(a.id).subscribe(() => {
      this.toasts.success(
        this.transloco.translate('agentDetail.toasts.agentPendingEnrollment', { name: a.name }),
      );
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
        this.toasts.success(this.transloco.translate('agentDetail.toasts.agentDeleted', { name: a.name }));
        this.router.navigate(['/agents']);
      },
      error: (e) => {
        this.deleteError.set(problemMessage(e));
        this.deletingAgent.set(false);
      },
    });
  }

  protected runPolicy(p: Policy): void {
    this.api
      .runPolicy(p.id)
      .subscribe(() =>
        this.toasts.success(this.transloco.translate('agentDetail.toasts.backupQueued', { name: p.name })),
      );
  }

  protected runAllPolicies(a: Agent): void {
    this.runningAll.set(true);
    this.api.runAllPolicies(a.id).subscribe({
      next: (jobs) => {
        this.runningAll.set(false);
        this.toasts.success(
          jobs.length > 0
            ? this.transloco.translate(
                jobs.length === 1 ? 'agentDetail.toasts.backupsQueuedOne' : 'agentDetail.toasts.backupsQueuedOther',
                { count: jobs.length },
              )
            : this.transloco.translate('agentDetail.toasts.nothingToRun'),
        );
        this.jobs.reload();
      },
      error: () => this.runningAll.set(false),
    });
  }

  protected async cancelJob(job: Job): Promise<void> {
    if (
      !(await this.confirm.ask(
        this.transloco.translate('agentDetail.confirms.cancelJobTitle'),
        this.transloco.translate('agentDetail.confirms.cancelJobBody', { type: job.type }),
        { confirmLabel: this.transloco.translate('agentDetail.confirms.cancelJobConfirm'), danger: true },
      ))
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
          this.toasts.success(this.transloco.translate('agentDetail.toasts.settingsSaved'));
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
        this.toasts.success(
          this.transloco.translate('agentDetail.toasts.connectionSaved', { name: connection.name }),
        );
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
      this.transloco.translate('agentDetail.confirms.deleteConnectionTitle', { name: c.name }),
      this.transloco.translate('agentDetail.confirms.deleteConnectionBody'),
      { confirmLabel: this.transloco.translate('common.delete'), danger: true },
    );
    if (!ok) return;
    this.api.deleteConnection(c.id).subscribe(() => {
      this.toasts.success(this.transloco.translate('agentDetail.toasts.connectionDeleted'));
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
          this.toasts.success(this.transloco.translate('agentDetail.toasts.credentialsUpdated'));
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
