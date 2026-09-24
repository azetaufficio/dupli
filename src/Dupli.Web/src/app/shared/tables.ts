import { Component, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { Alert, Job, LogEntry, Run, TERMINAL_STATES } from '../core/models';
import { Badge } from './badge';
import { BytesPipe, DateTimePipe, DurationPipe, RelativeTimePipe } from './format';

/** Maps ids to display names in shared tables (agents, policies). */
export type NameLookup = Record<string, string>;

@Component({
  selector: 'app-jobs-table',
  imports: [Badge, DateTimePipe, DurationPipe, RouterLink],
  template: `
    @if (jobs().length === 0) {
      <div class="empty">No jobs.</div>
    } @else {
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Type</th>
              @if (showAgent()) {
                <th>Agent</th>
              }
              <th>Policy</th>
              <th>Trigger</th>
              <th>State</th>
              <th>Scheduled</th>
              <th>Duration</th>
              <th>Error</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (job of jobs(); track job.id) {
              <tr>
                <td class="nowrap">{{ job.type }}</td>
                @if (showAgent()) {
                  <td>
                    <a [routerLink]="['/agents', job.agentId]">{{
                      agents()[job.agentId] ?? job.agentId
                    }}</a>
                  </td>
                }
                <td>{{ job.policyId ? (policies()[job.policyId] ?? '—') : '—' }}</td>
                <td>{{ job.trigger }}</td>
                <td>
                  <app-badge [value]="job.state" />
                  @if (job.cancelRequested && !isTerminal(job)) {
                    <span class="muted"> cancelling…</span>
                  }
                </td>
                <td class="nowrap">{{ job.scheduledAt | datetime }}</td>
                <td class="nowrap">{{ job.startedAt | duration: job.completedAt }}</td>
                <td class="muted">{{ job.error }}</td>
                <td class="num">
                  @if (!isTerminal(job) && !job.cancelRequested && auth.canOperate()) {
                    <button type="button" class="btn small danger" (click)="cancel.emit(job)">
                      Cancel
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
  readonly agents = input<NameLookup>({});
  readonly policies = input<NameLookup>({});
  readonly showAgent = input(false);
  readonly cancel = output<Job>();

  protected isTerminal(job: Job): boolean {
    return TERMINAL_STATES.includes(job.state);
  }
}

@Component({
  selector: 'app-items-table',
  imports: [Badge, BytesPipe],
  template: `
    @if (items().length === 0) {
      <span class="muted">No item results.</span>
    } @else {
      <table>
        <thead>
          <tr>
            <th>Source</th>
            <th>Item</th>
            <th>Outcome</th>
            <th>Snapshot</th>
            <th class="num">Bytes</th>
            <th>Details</th>
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
  imports: [Badge, BytesPipe, DateTimePipe, DurationPipe, ItemsTable, RouterLink],
  template: `
    @if (runs().length === 0) {
      <div class="empty">No backup runs yet.</div>
    } @else {
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Completed</th>
              @if (showAgent()) {
                <th>Agent</th>
              }
              <th>Policy</th>
              <th>Status</th>
              <th>Duration</th>
              <th class="num">Processed</th>
              <th class="num">Added</th>
              <th>Error</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (run of runs(); track run.id) {
              <tr>
                <td class="nowrap">{{ run.completedAt | datetime }}</td>
                @if (showAgent()) {
                  <td>
                    <a [routerLink]="['/agents', run.agentId]">{{
                      agents()[run.agentId] ?? run.agentId
                    }}</a>
                  </td>
                }
                <td>{{ policies()[run.policyId] ?? '—' }}</td>
                <td><app-badge [value]="run.status" /></td>
                <td class="nowrap">{{ run.startedAt | duration: run.completedAt }}</td>
                <td class="num">{{ run.bytesProcessed | bytes }}</td>
                <td class="num">{{ run.bytesAdded | bytes }}</td>
                <td class="muted">{{ run.errorMessage }}</td>
                <td class="num">
                  <button type="button" class="link" (click)="toggle(run.id)">
                    {{ expanded() === run.id ? 'Hide' : 'Items (' + run.items.length + ')' }}
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
  readonly agents = input<NameLookup>({});
  readonly policies = input<NameLookup>({});
  readonly showAgent = input(false);
  protected readonly expanded = signal<string | null>(null);

  protected toggle(id: string): void {
    this.expanded.update((current) => (current === id ? null : id));
  }
}

@Component({
  selector: 'app-logs-table',
  imports: [Badge, DateTimePipe, RouterLink],
  template: `
    @if (logs().length === 0) {
      <div class="empty">No log entries.</div>
    } @else {
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Time</th>
              @if (showAgent()) {
                <th>Agent</th>
              }
              <th>Level</th>
              <th>Message</th>
            </tr>
          </thead>
          <tbody>
            @for (log of logs(); track log.id) {
              <tr>
                <td class="nowrap">{{ log.timestamp | datetime }}</td>
                @if (showAgent()) {
                  <td class="nowrap">
                    <a [routerLink]="['/agents', log.agentId]">{{
                      agents()[log.agentId] ?? log.agentId
                    }}</a>
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
  readonly agents = input<NameLookup>({});
  readonly showAgent = input(false);
}

@Component({
  selector: 'app-alerts-table',
  imports: [Badge, DateTimePipe, RelativeTimePipe, RouterLink],
  template: `
    @if (alerts().length === 0) {
      <div class="empty">No alerts. All good.</div>
    } @else {
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Status</th>
              <th>Kind</th>
              @if (showAgent()) {
                <th>Agent</th>
              }
              <th>Message</th>
              <th>Opened</th>
              <th>Resolved</th>
            </tr>
          </thead>
          <tbody>
            @for (alert of alerts(); track alert.id) {
              <tr>
                <td><app-badge [value]="alert.resolvedAt ? 'Resolved' : 'Open'" /></td>
                <td class="nowrap">{{ kindLabel(alert) }}</td>
                @if (showAgent()) {
                  <td class="nowrap">
                    @if (alert.agentId) {
                      <a [routerLink]="['/agents', alert.agentId]">{{
                        agents()[alert.agentId] ?? alert.agentId
                      }}</a>
                    }
                  </td>
                }
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
  readonly agents = input<NameLookup>({});
  readonly showAgent = input(false);

  protected kindLabel(alert: Alert): string {
    return alert.kind.replace(/([a-z])([A-Z])/g, '$1 $2');
  }
}

/** Builds an id → name lookup for the tables. */
export function lookup<T extends { id: string; name: string }>(
  items: readonly T[] | undefined,
): NameLookup {
  return Object.fromEntries((items ?? []).map((i) => [i.id, i.name]));
}
