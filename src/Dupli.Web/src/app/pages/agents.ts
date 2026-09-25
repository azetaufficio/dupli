import { HttpContext, httpResource } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslocoModule } from '@jsverse/transloco';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import { Agent, CreateAgentRequest, StorageTarget } from '../core/models';
import { agentUpdateStatus } from '../shared/agent-update-status';
import { Badge } from '../shared/badge';
import { DateTimePipe, RelativeTimePipe } from '../shared/format';
import { lookup } from '../shared/tables';

@Component({
  selector: 'app-agents',
  imports: [Badge, DateTimePipe, FormsModule, RelativeTimePipe, RouterLink, TranslocoModule],
  template: `
    <div class="page-header">
      <div>
        <h1>{{ 'agents.title' | transloco }}</h1>
        <p class="muted">{{ 'agents.subtitle' | transloco }}</p>
      </div>
      @if (auth.canOperate()) {
        <div class="toolbar">
          <button type="button" class="btn primary" (click)="showForm.set(!showForm())">
            {{ (showForm() ? 'common.close' : 'agents.newAgent') | transloco }}
          </button>
        </div>
      }
    </div>

    @if (showForm()) {
      <section class="card">
        <h2>{{ 'agents.newAgent' | transloco }}</h2>
        @if (storageTargets.value()?.length === 0) {
          <p class="error-box">
            {{ 'agents.noStorageTarget' | transloco }}
            <a routerLink="/storage">{{ 'agents.createStorageFirst' | transloco }}</a>
          </p>
        }
        <form class="form" (ngSubmit)="create()" #f="ngForm">
          <div class="form-row">
            <label class="field"
              >{{ 'agents.form.name' | transloco }}
              <input name="name" [(ngModel)]="form.name" required
            /></label>
            <label class="field">
              {{ 'agents.form.storageTarget' | transloco }}
              <select name="storageTargetId" [(ngModel)]="form.storageTargetId" required>
                @for (s of storageTargets.value() ?? []; track s.id) {
                  <option [value]="s.id">{{ s.name }} ({{ s.bucket }})</option>
                }
              </select>
            </label>
            <label class="field">
              {{ 'agents.form.storagePrefix' | transloco }}
              <input
                name="storagePrefix"
                [(ngModel)]="form.storagePrefix"
                required
                placeholder="agents/vm-01"
              />
              <span class="hint">{{ 'agents.form.storagePrefixHint' | transloco }}</span>
            </label>
          </div>
          <div class="form-row">
            <label class="field"
              >{{ 'agents.form.s3AccessKeyId' | transloco }}
              <input name="ak" [(ngModel)]="form.s3AccessKeyId" required autocomplete="off"
            /></label>
            <label class="field">
              {{ 'agents.form.s3SecretAccessKey' | transloco }}
              <input
                name="sk"
                type="password"
                [(ngModel)]="form.s3SecretAccessKey"
                required
                autocomplete="new-password"
              />
            </label>
            <label class="field">
              {{ 'agents.form.repositoryPassword' | transloco }}
              <span class="hint">{{ 'agents.form.repositoryPasswordHint' | transloco }}</span>
              <input
                name="rp"
                type="password"
                [(ngModel)]="form.repositoryPassword"
                autocomplete="new-password"
              />
            </label>
          </div>
          @if (error()) {
            <div class="error-box">{{ error() }}</div>
          }
          <div class="toolbar">
            <button type="submit" class="btn primary" [disabled]="f.invalid || saving()">
              {{ 'agents.form.submit' | transloco }}
            </button>
            <span class="muted">{{ 'agents.form.secretsNotice' | transloco }}</span>
          </div>
        </form>
      </section>
    }

    <section class="card flush">
      @if ((agents.value() ?? []).length === 0) {
        <div class="empty">{{ 'agents.empty' | transloco }}</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{{ 'agents.table.name' | transloco }}</th>
                <th>{{ 'agents.table.status' | transloco }}</th>
                <th>{{ 'agents.table.hostname' | transloco }}</th>
                <th>{{ 'agents.table.agent' | transloco }}</th>
                <th>{{ 'agents.table.storage' | transloco }}</th>
                <th>{{ 'agents.table.lastHeartbeat' | transloco }}</th>
                <th>{{ 'agents.table.enrolled' | transloco }}</th>
              </tr>
            </thead>
            <tbody>
              @for (a of agents.value() ?? []; track a.id) {
                <tr>
                  <td>
                    <a [routerLink]="['/agents', a.id]">{{ a.name }}</a>
                  </td>
                  <td>
                    @if (a.status === 'Active') {
                      <app-badge [value]="a.online ? 'Online' : 'Offline'" />
                    } @else {
                      <app-badge [value]="a.status" />
                    }
                  </td>
                  <td>{{ a.hostname ?? '—' }}</td>
                  <td class="nowrap">
                    <span class="mono">{{ a.version ?? '—' }}</span>
                    <app-badge [value]="agentUpdateStatus(a)" />
                  </td>
                  <td class="mono">
                    {{ storageNames()[a.storageTargetId] ?? '?' }}/{{ a.storagePrefix }}
                  </td>
                  <td class="nowrap">{{ a.lastHeartbeatAt | relative }}</td>
                  <td class="nowrap">{{ a.enrolledAt | datetime }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
})
export class AgentsPage {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  protected readonly agentUpdateStatus = agentUpdateStatus;

  protected readonly agents = httpResource<Agent[]>(() => '/api/admin/agents');
  protected readonly storageTargets = httpResource<StorageTarget[]>(
    () => '/api/admin/storage-targets',
  );
  protected readonly storageNames = computed(() => lookup(this.storageTargets.value()));

  protected readonly showForm = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected form: CreateAgentRequest = this.empty();

  private empty(): CreateAgentRequest {
    return {
      name: '',
      storageTargetId: '',
      storagePrefix: '',
      s3AccessKeyId: '',
      s3SecretAccessKey: '',
      repositoryPassword: '',
    };
  }

  protected create(): void {
    this.saving.set(true);
    this.error.set(null);
    const request = { ...this.form, repositoryPassword: this.form.repositoryPassword || null };
    this.api.createAgent(request, new HttpContext().set(SILENT_ERRORS, true)).subscribe({
      next: (agent) => {
        this.form = this.empty();
        this.router.navigate(['/agents', agent.id]);
      },
      error: (e) => {
        this.error.set(problemMessage(e));
        this.saving.set(false);
      },
    });
  }
}
