import { httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoModule } from '@jsverse/transloco';
import { ApiService } from '../core/api.service';
import { Agent } from '../core/models';
import { PagedList } from '../shared/paged-list';
import { LogsTable } from '../shared/tables';

@Component({
  selector: 'app-logs',
  imports: [FormsModule, LogsTable, TranslocoModule],
  template: `
    <div class="page-header">
      <div>
        <h1>{{ 'logs.title' | transloco }}</h1>
        <p class="muted">{{ 'logs.subtitle' | transloco }}</p>
      </div>
      <div class="toolbar">
        <select [ngModel]="agentId()" (ngModelChange)="onFilterChange('agent', $event)" style="width: auto">
          <option value="">{{ 'logs.allAgents' | transloco }}</option>
          @for (a of agents.value() ?? []; track a.id) {
            <option [value]="a.id">{{ a.name }}</option>
          }
        </select>
        <select [ngModel]="level()" (ngModelChange)="onFilterChange('level', $event)" style="width: auto">
          <option value="">{{ 'logs.allLevels' | transloco }}</option>
          <option value="Information">{{ 'logs.levels.information' | transloco }}</option>
          <option value="Warning">{{ 'logs.levels.warning' | transloco }}</option>
          <option value="Error">{{ 'logs.levels.error' | transloco }}</option>
        </select>
        <button type="button" class="btn" (click)="logs.reload()">
          {{ 'logs.refresh' | transloco }}
        </button>
      </div>
    </div>
    <section class="card flush">
      <app-logs-table [logs]="logs.items()" [showAgent]="true" />
      @if (logs.next()) {
        <div class="toolbar" style="padding: 1rem">
          <button
            type="button"
            class="btn"
            [disabled]="logs.loadingMore()"
            (click)="logs.loadMore()"
          >
            {{ logs.loadingMore() ? ('common.loading' | transloco) : ('logs.loadMore' | transloco) }}
          </button>
        </div>
      }
    </section>
  `,
})
export class LogsPage {
  private readonly api = inject(ApiService);

  protected readonly agentId = signal('');
  protected readonly level = signal('');
  protected readonly agents = httpResource<Agent[]>(() => '/api/admin/agents');
  protected readonly logs = new PagedList((before) =>
    this.api.logs({
      agentId: this.agentId() || undefined,
      level: this.level() || undefined,
      before,
      limit: 200,
    }),
  );

  constructor() {
    this.logs.reload();
  }

  protected onFilterChange(field: 'agent' | 'level', value: string): void {
    if (field === 'agent') this.agentId.set(value);
    else this.level.set(value);
    this.logs.reload();
  }
}
