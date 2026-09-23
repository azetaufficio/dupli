import { httpResource } from '@angular/common/http';
import { Component, DestroyRef, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Dashboard } from '../core/models';
import { agentUpdateStatus } from '../shared/agent-update-status';
import { Badge } from '../shared/badge';
import { DateTimePipe, RelativeTimePipe } from '../shared/format';

const REFRESH_MS = 15_000;

@Component({
  selector: 'app-dashboard',
  imports: [Badge, DateTimePipe, RelativeTimePipe, RouterLink],
  template: `
    <div class="page-header">
      <div>
        <h1>Dashboard</h1>
        <p class="muted">Refreshes every 15 seconds.</p>
      </div>
      <div class="toolbar">
        <a class="btn" routerLink="/agents">Manage agents</a>
      </div>
    </div>

    @if (dashboard.value(); as d) {
      <div class="grid counters">
        <div class="counter ok">
          <div class="value">{{ d.counters.online }}</div>
          <div class="label">Online</div>
        </div>
        <div class="counter" [class.bad]="d.counters.offline > 0">
          <div class="value">{{ d.counters.offline }}</div>
          <div class="label">Offline</div>
        </div>
        <div class="counter" [class.warn]="d.counters.pending > 0">
          <div class="value">{{ d.counters.pending }}</div>
          <div class="label">Pending enrollment</div>
        </div>
        <div class="counter" [class.bad]="d.counters.backupFailed > 0">
          <div class="value">{{ d.counters.backupFailed }}</div>
          <div class="label">Backup failed</div>
        </div>
        <div class="counter info">
          <div class="value">{{ d.counters.backupRunning }}</div>
          <div class="label">Backup running</div>
        </div>
        <a
          class="counter"
          routerLink="/alerts"
          [class.bad]="d.counters.openAlerts > 0"
          style="text-decoration: none; color: inherit"
        >
          <div class="value">{{ d.counters.openAlerts }}</div>
          <div class="label">Open alerts</div>
        </a>
      </div>

      <section class="card flush">
        <div class="card-header"><h2>Agents</h2></div>
        @if (d.agents.length === 0) {
          <div class="empty">No agents yet. <a routerLink="/agents">Create the first one.</a></div>
        } @else {
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Hostname</th>
                  <th>Status</th>
                  <th>Agent</th>
                  <th>restic</th>
                  <th>Last heartbeat</th>
                  <th>Last backup</th>
                  <th>Last run</th>
                  <th>Activity</th>
                  <th class="num">Alerts</th>
                </tr>
              </thead>
              <tbody>
                @for (row of d.agents; track row.agent.id) {
                  <tr>
                    <td>
                      <a [routerLink]="['/agents', row.agent.id]">{{ row.agent.name }}</a>
                    </td>
                    <td>{{ row.agent.hostname ?? '—' }}</td>
                    <td>
                      @if (row.agent.status === 'Active') {
                        <app-badge [value]="row.agent.online ? 'Online' : 'Offline'" />
                      } @else {
                        <app-badge [value]="row.agent.status" />
                      }
                    </td>
                    <td class="nowrap">
                      <span class="mono">{{ row.agent.version ?? '—' }}</span>
                      <app-badge [value]="agentUpdateStatus(row.agent)" />
                    </td>
                    <td class="mono">{{ row.agent.resticVersion ?? '—' }}</td>
                    <td class="nowrap" [title]="row.agent.lastHeartbeatAt | datetime">
                      {{ row.agent.lastHeartbeatAt | relative }}
                    </td>
                    <td class="nowrap" [title]="row.agent.lastBackupAt | datetime">
                      {{ row.agent.lastBackupAt | relative }}
                    </td>
                    <td>
                      @if (row.lastRunStatus) {
                        <app-badge [value]="row.lastRunStatus" />
                      } @else {
                        <span class="muted">—</span>
                      }
                    </td>
                    <td>
                      @if (row.runningJob) {
                        <app-badge value="Running" [text]="row.runningJob" />
                      } @else {
                        <span class="muted">idle</span>
                      }
                    </td>
                    <td class="num">
                      @if (row.openAlerts > 0) {
                        <app-badge value="Open" [text]="'' + row.openAlerts" />
                      } @else {
                        <span class="muted">0</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
    } @else if (dashboard.isLoading()) {
      <p class="muted">Loading…</p>
    }
  `,
})
export class DashboardPage {
  protected readonly agentUpdateStatus = agentUpdateStatus;

  protected readonly dashboard = httpResource<Dashboard>(() => '/api/admin/dashboard');

  constructor() {
    const timer = setInterval(() => this.dashboard.reload(), REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }
}
