import { httpResource } from '@angular/common/http';
import { Component, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Agent, Policy, Run } from '../core/models';
import { RunsTable, lookup } from '../shared/tables';

@Component({
  selector: 'app-history',
  imports: [FormsModule, RunsTable],
  template: `
    <div class="page-header">
      <div>
        <h1>Backup history</h1>
        <p class="muted">Most recent 200 runs.</p>
      </div>
      <div class="toolbar">
        <select [ngModel]="agentId()" (ngModelChange)="agentId.set($event)" style="width: auto">
          <option value="">All agents</option>
          @for (a of agents.value() ?? []; track a.id) {
            <option [value]="a.id">{{ a.name }}</option>
          }
        </select>
        <button type="button" class="btn" (click)="runs.reload()">Refresh</button>
      </div>
    </div>
    <section class="card flush">
      <app-runs-table
        [runs]="runs.value() ?? []"
        [agents]="agentNames()"
        [policies]="policyNames()"
        [showAgent]="true"
      />
    </section>
  `,
})
export class HistoryPage {
  protected readonly agentId = signal('');
  protected readonly agents = httpResource<Agent[]>(() => '/api/admin/agents');
  protected readonly policies = httpResource<Policy[]>(() => '/api/admin/policies');
  protected readonly runs = httpResource<Run[]>(() => ({
    url: '/api/admin/runs',
    params: { agentId: this.agentId(), limit: 200 },
  }));
  protected readonly agentNames = computed(() => lookup(this.agents.value()));
  protected readonly policyNames = computed(() => lookup(this.policies.value()));
}
