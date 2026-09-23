import { httpResource } from '@angular/common/http';
import { Component, computed, signal } from '@angular/core';
import { Agent, Alert } from '../core/models';
import { AlertsTable, lookup } from '../shared/tables';

@Component({
  selector: 'app-alerts',
  imports: [AlertsTable],
  template: `
    <div class="page-header">
      <div>
        <h1>Alerts</h1>
        <p class="muted">
          Evaluated every minute. Notifications are sent by e-mail when an alert opens and when it
          resolves.
        </p>
      </div>
      <div class="toolbar">
        <button type="button" class="btn" [class.primary]="openOnly()" (click)="openOnly.set(true)">
          Open
        </button>
        <button
          type="button"
          class="btn"
          [class.primary]="!openOnly()"
          (click)="openOnly.set(false)"
        >
          All
        </button>
      </div>
    </div>
    <section class="card flush">
      <app-alerts-table
        [alerts]="alerts.value() ?? []"
        [agents]="agentNames()"
        [showAgent]="true"
      />
    </section>
  `,
})
export class AlertsPage {
  protected readonly openOnly = signal(true);
  protected readonly agents = httpResource<Agent[]>(() => '/api/admin/agents');
  protected readonly alerts = httpResource<Alert[]>(() => ({
    url: '/api/admin/alerts',
    params: { open: this.openOnly(), limit: 200 },
  }));
  protected readonly agentNames = computed(() => lookup(this.agents.value()));
}
