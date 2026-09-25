import { Component, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoModule } from '@jsverse/transloco';
import { AuthService } from '../core/auth.service';
import { Alert, Job, LogEntry, Run, TERMINAL_STATES } from '../core/models';
import { Badge } from './badge';
import { BytesPipe, DateTimePipe, DurationPipe, RelativeTimePipe } from './format';

/** Maps ids to display names in shared tables (agents, policies). */
export type NameLookup = Record<string, string>;

@Component({
  selector: 'app-jobs-table',
  imports: [Badge, DateTimePipe, DurationPipe, RouterLink, TranslocoModule],
  template: `
    @if (jobs().length === 0) {
      <div class="empty">{{ 'tables.jobs.empty' | transloco }}</div>
    } @else {
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>{{ 'tables.jobs.type' | transloco }}</th>
              @if (showAgent()) {
                <th>{{ 'tables.jobs.agent' | transloco }}</th>
              }
              <th>{{ 'tables.jobs.policy' | transloco }}</th>
              <th>{{ 'tables.jobs.trigger' | transloco }}</th>
              <th>{{ 'tables.jobs.state' | transloco }}</th>
              <th>{{ 'tables.jobs.scheduled' | transloco }}</th>
              <th>{{ 'tables.jobs.duration' | transloco }}</th>
              <th>{{ 'tables.jobs.error' | transloco }}</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (job of jobs(); track job.id) {
              <tr>
                <td class="nowrap">{{ 'jobType.' + job.type | transloco }}</td>
                @if (showAgent()) {
                  <td>
                    <a [routerLink]="['/agents', job.agentId]">{{ job.agentName }}</a>
                  </td>
                }
                <td>{{ job.policyName ?? '—' }}</td>
                <td>{{ 'jobTrigger.' + job.trigger | transloco }}</td>
                <td>
                  <app-badge [value]="job.state" />
                  @if (job.cancelRequested && !isTerminal(job)) {
                    <span class="muted"> {{ 'tables.jobs.cancelling' | transloco }}</span>
                  }
                </td>
                <td class="nowrap">{{ job.scheduledAt | datetime }}</td>
                <td class="nowrap">{{ job.startedAt | duration: job.completedAt }}</td>
                <td class="muted">{{ job.error }}</td>
                <td class="num">
                  @if (!isTerminal(job) && !job.cancelRequested && auth.canOperate()) {
                    <button type="button" class="btn small danger" (click)="cancel.emit(job)">
                      {{ 'common.cancel' | transloco }}
                    </button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class JobsTable {
  protected readonly auth = inject(AuthService);
  readonly jobs = input.required<Job[]>();
  readonly showAgent = input(false);
  readonly cancel = output<Job>();

  protected isTerminal(job: Job): boolean {
    return TERMINAL_STATES.includes(job.state);
  }
}

@Component({
  selector: 'app-items-table',
  imports: [Badge, BytesPipe, TranslocoModule],
  template: `
    @if (items().length === 0) {
      <span class="muted">{{ 'tables.items.empty' | transloco }}</span>
    } @else {
      <table>
        <thead>
          <tr>
            <th>{{ 'tables.items.source' | transloco }}</th>
            <th>{{ 'tables.items.item' | transloco }}</th>
            <th>{{ 'tables.items.outcome' | transloco }}</th>
            <th>{{ 'tables.items.snapshot' | transloco }}</th>
            <th class="num">{{ 'tables.items.bytes' | transloco }}</th>
            <th>{{ 'tables.items.details' | transloco }}</th>
          </tr>
        </thead>
        <tbody>
          @for (item of items(); track $index) {
            <tr>
              <td class="nowrap">{{ item.sourceId }}</td>
              <td class="mono">{{ item.item }}</td>
              <td><app-badge [value]="item.outcome" /></td>
              <td class="mono">{{ item.snapshotId?.slice(0, 8) ?? '—' }}</td>
              <td class="num">{{ item.bytesProcessed | bytes }}</td>
              <td class="muted">
                @if (item.location) {
                  <div class="mono">→ {{ item.location }}</div>
                }
                {{ item.error }}
                @for (w of item.warnings; track $index) {
                  <div>⚠ {{ w }}</div>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
})
export class ItemsTable {
  readonly items = input.required<Run['items']>();
}

@Component({
  selector: 'app-runs-table',
  imports: [Badge, BytesPipe, DateTimePipe, DurationPipe, ItemsTable, RouterLink, TranslocoModule],
  template: `
    @if (runs().length === 0) {
      <div class="empty">{{ 'tables.runs.empty' | transloco }}</div>
    } @else {
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>{{ 'tables.runs.completed' | transloco }}</th>
              @if (showAgent()) {
                <th>{{ 'tables.runs.agent' | transloco }}</th>
              }
              <th>{{ 'tables.runs.policy' | transloco }}</th>
              <th>{{ 'tables.runs.status' | transloco }}</th>
              <th>{{ 'tables.runs.duration' | transloco }}</th>
              <th class="num">{{ 'tables.runs.processed' | transloco }}</th>
              <th class="num">{{ 'tables.runs.added' | transloco }}</th>
              <th>{{ 'tables.runs.error' | transloco }}</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (run of runs(); track run.id) {
              <tr>
                <td class="nowrap">{{ run.completedAt | datetime }}</td>
                @if (showAgent()) {
                  <td>
                    <a [routerLink]="['/agents', run.agentId]">{{ run.agentName }}</a>
                  </td>
                }
                <td>{{ run.policyName ?? '—' }}</td>
                <td><app-badge [value]="run.status" /></td>
                <td class="nowrap">{{ run.startedAt | duration: run.completedAt }}</td>
                <td class="num">{{ run.bytesProcessed | bytes }}</td>
                <td class="num">{{ run.bytesAdded | bytes }}</td>
                <td class="muted">{{ run.errorMessage }}</td>
                <td class="num">
                  <button type="button" class="link" (click)="toggle(run.id)">
                    {{
                      expanded() === run.id
                        ? ('tables.runs.hide' | transloco)
                        : ('tables.runs.items' | transloco: { count: run.items.length })
                    }}
                  </button>
                </td>
              </tr>
              @if (expanded() === run.id) {
                <tr class="detail">
                  <td [attr.colspan]="showAgent() ? 9 : 8">
                    <app-items-table [items]="run.items" />
                  </td>
                </tr>
              }
            }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class RunsTable {
  readonly runs = input.required<Run[]>();
  readonly showAgent = input(false);
  protected readonly expanded = signal<string | null>(null);

  protected toggle(id: string): void {
    this.expanded.update((current) => (current === id ? null : id));
  }
}

@Component({
  selector: 'app-logs-table',
  imports: [Badge, DateTimePipe, RouterLink, TranslocoModule],
  template: `
    @if (logs().length === 0) {
      <div class="empty">{{ 'tables.logs.empty' | transloco }}</div>
    } @else {
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>{{ 'tables.logs.time' | transloco }}</th>
              @if (showAgent()) {
                <th>{{ 'tables.logs.agent' | transloco }}</th>
              }
              <th>{{ 'tables.logs.level' | transloco }}</th>
              <th>{{ 'tables.logs.message' | transloco }}</th>
            </tr>
          </thead>
          <tbody>
            @for (log of logs(); track log.id) {
              <tr>
                <td class="nowrap">{{ log.timestamp | datetime }}</td>
                @if (showAgent()) {
                  <td class="nowrap">
                    <a [routerLink]="['/agents', log.agentId]">{{ log.agentName }}</a>
                  </td>
                }
                <td><app-badge [value]="log.level" /></td>
                <td>
                  {{ log.message }}
                  @if (log.exception) {
                    <pre class="log">{{ log.exception }}</pre>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class LogsTable {
  readonly logs = input.required<LogEntry[]>();
  readonly showAgent = input(false);
}

@Component({
  selector: 'app-alerts-table',
  imports: [Badge, DateTimePipe, RelativeTimePipe, RouterLink, TranslocoModule],
  template: `
    @if (alerts().length === 0) {
      <div class="empty">{{ 'tables.alerts.empty' | transloco }}</div>
    } @else {
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>{{ 'tables.alerts.status' | transloco }}</th>
              <th>{{ 'tables.alerts.kind' | transloco }}</th>
              @if (showAgent()) {
                <th>{{ 'tables.alerts.agent' | transloco }}</th>
              }
              <th>{{ 'tables.alerts.policy' | transloco }}</th>
              <th>{{ 'tables.alerts.message' | transloco }}</th>
              <th>{{ 'tables.alerts.opened' | transloco }}</th>
              <th>{{ 'tables.alerts.resolved' | transloco }}</th>
            </tr>
          </thead>
          <tbody>
            @for (alert of alerts(); track alert.id) {
              <tr>
                <td><app-badge [value]="alert.resolvedAt ? 'Resolved' : 'Open'" /></td>
                <td class="nowrap">{{ 'alertKind.' + alert.kind + '.label' | transloco }}</td>
                @if (showAgent()) {
                  <td class="nowrap">
                    @if (alert.agentId) {
                      <a [routerLink]="['/agents', alert.agentId]">{{ alert.agentName }}</a>
                    }
                  </td>
                }
                <td>{{ alert.policyName ?? '—' }}</td>
                <td>{{ alert.message }}</td>
                <td class="nowrap" [title]="alert.openedAt | datetime">
                  {{ alert.openedAt | relative }}
                </td>
                <td class="nowrap">{{ alert.resolvedAt | datetime }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class AlertsTable {
  readonly alerts = input.required<Alert[]>();
  readonly showAgent = input(false);
}

/** Builds an id → name lookup for the tables. */
export function lookup<T extends { id: string; name: string }>(
  items: readonly T[] | undefined,
): NameLookup {
  return Object.fromEntries((items ?? []).map((i) => [i.id, i.name]));
}
