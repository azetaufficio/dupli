import { httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoModule } from '@jsverse/transloco';
import { ApiService } from '../core/api.service';
import { Agent } from '../core/models';
import { PagedList } from '../shared/paged-list';
import { RunsTable } from '../shared/tables';

@Component({
  selector: 'app-history',
  imports: [FormsModule, RunsTable, TranslocoModule],
  template: `
    <div class="page-header">
      <div>
        <h1>{{ 'history.title' | transloco }}</h1>
        <p class="muted">{{ 'history.subtitle' | transloco }}</p>
      </div>
      <div class="toolbar">
        <select [ngModel]="agentId()" (ngModelChange)="onAgentChange($event)" style="width: auto">
          <option value="">{{ 'history.allAgents' | transloco }}</option>
          @for (a of agents.value() ?? []; track a.id) {
            <option [value]="a.id">{{ a.name }}</option>
          }
        </select>
        <button type="button" class="btn" (click)="runs.reload()">
          {{ 'history.refresh' | transloco }}
        </button>
      </div>
    </div>
    <section class="card flush">
      <app-runs-table [runs]="runs.items()" [showAgent]="true" />
      @if (runs.next()) {
        <div class="toolbar" style="padding: 1rem">
          <button
            type="button"
            class="btn"
            [disabled]="runs.loadingMore()"
            (click)="runs.loadMore()"
          >
            {{ runs.loadingMore() ? ('common.loading' | transloco) : ('history.loadMore' | transloco) }}
          </button>
        </div>
      }
    </section>
  `,
})
export class HistoryPage {
  private readonly api = inject(ApiService);

  protected readonly agentId = signal('');
  protected readonly agents = httpResource<Agent[]>(() => '/api/admin/agents');
  protected readonly runs = new PagedList((before) =>
    this.api.runs({ agentId: this.agentId() || undefined, before, limit: 100 }),
  );

  constructor() {
    this.runs.reload();
  }

  protected onAgentChange(agentId: string): void {
    this.agentId.set(agentId);
    this.runs.reload();
  }
}
