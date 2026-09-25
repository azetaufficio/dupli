import { httpResource } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { TranslocoModule } from '@jsverse/transloco';
import { Alert } from '../core/models';
import { AlertsTable } from '../shared/tables';

@Component({
  selector: 'app-alerts',
  imports: [AlertsTable, TranslocoModule],
  template: `
    <div class="page-header">
      <div>
        <h1>{{ 'alerts.title' | transloco }}</h1>
        <p class="muted">{{ 'alerts.subtitle' | transloco }}</p>
      </div>
      <div class="toolbar">
        <button type="button" class="btn" [class.primary]="openOnly()" (click)="openOnly.set(true)">
          {{ 'alerts.open' | transloco }}
        </button>
        <button
          type="button"
          class="btn"
          [class.primary]="!openOnly()"
          (click)="openOnly.set(false)"
        >
          {{ 'alerts.all' | transloco }}
        </button>
      </div>
    </div>
    <section class="card flush">
      <app-alerts-table [alerts]="alerts.value() ?? []" [showAgent]="true" />
    </section>
  `,
})
export class AlertsPage {
  protected readonly openOnly = signal(true);
  protected readonly alerts = httpResource<Alert[]>(() => ({
    url: '/api/admin/alerts',
    params: { open: this.openOnly(), limit: 200 },
  }));
}
