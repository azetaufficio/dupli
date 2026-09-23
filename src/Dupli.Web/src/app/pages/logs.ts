import { httpResource } from '@angular/common/http';
import { Component, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { params } from '../core/api.service';
import { Agent, LogEntry } from '../core/models';
import { LogsTable, lookup } from '../shared/tables';

@Component({
  selector: 'app-logs',
  imports: [FormsModule, LogsTable],
  template: `
    <div class="page-header">
      <div>
        <h1>Agent logs</h1>
        <p class="muted">Information and above, uploaded by the agents in batches.</p>
      </div>
      <div class="toolbar">
        <select [ngModel]="agentId()" (ngModelChange)="agentId.set($event)" style="width: auto">
          <option value="">All agents</option>
          @for (a of agents.value() ?? []; track a.id) {
            <option [value]="a.id">{{ a.name }}</option>
          }
        </select>
        <select [ngModel]="level()" (ngModelChange)="level.set($event)" style="width: auto">
          <option value="">All levels</option>
          <option value="Information">Information</option>
          <option value="Warning">Warning</option>
          <option value="Error">Error</option>
        </select>
        <button type="button" class="btn" (click)="logs.reload()">Refresh</button>
      </div>
    </div>
    <section class="card flush">
      <app-logs-table [logs]="logs.value() ?? []" [agents]="agentNames()" [showAgent]="true" />
    </section>
  `,
})
export class LogsPage {
  protected readonly agentId = signal('');
  protected readonly level = signal('');
  protected readonly agents = httpResource<Agent[]>(() => '/api/admin/agents');
  protected readonly logs = httpResource<LogEntry[]>(() => ({
    url: '/api/admin/logs',
    params: params({ agentId: this.agentId(), level: this.level(), limit: 500 }),
  }));
  protected readonly agentNames = computed(() => lookup(this.agents.value()));
}
