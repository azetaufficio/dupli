import { HttpContext, httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import { CreateStorageTargetRequest, StorageTarget } from '../core/models';
import { ToastService } from '../core/toast.service';

@Component({
  selector: 'app-storage',
  imports: [FormsModule, TranslocoModule],
  template: `
    <div class="page-header">
      <div>
        <h1>{{ 'storage.title' | transloco }}</h1>
        <p class="muted">{{ 'storage.subtitle' | transloco }}</p>
      </div>
    </div>

    @if (auth.isOwner()) {
      <section class="card">
        <h2>{{ 'storage.newTarget' | transloco }}</h2>
        <form class="form" (ngSubmit)="create()" #f="ngForm">
          <div class="form-row">
            <label class="field"
              >{{ 'storage.form.name' | transloco }}
              <input name="name" [(ngModel)]="form.name" required placeholder="wasabi-eu"
            /></label>
            <label class="field">
              {{ 'storage.form.endpoint' | transloco }}
              <input
                name="endpoint"
                type="url"
                [(ngModel)]="form.endpoint"
                required
                placeholder="https://s3.eu-central-1.wasabisys.com"
              />
            </label>
            <label class="field"
              >{{ 'storage.form.bucket' | transloco }}
              <input name="bucket" [(ngModel)]="form.bucket" required
            /></label>
            <label class="field"
              >{{ 'storage.form.region' | transloco }}
              <span class="hint">{{ 'storage.form.optional' | transloco }}</span
              ><input name="region" [(ngModel)]="form.region"
            /></label>
          </div>
          @if (error()) {
            <div class="error-box">{{ error() }}</div>
          }
          <div class="toolbar">
            <button type="submit" class="btn primary" [disabled]="f.invalid || saving()">
              {{ 'storage.form.create' | transloco }}
            </button>
            <span class="muted">{{ 'storage.form.versioningHint' | transloco }}</span>
          </div>
        </form>
      </section>
    }

    <section class="card flush">
      @if ((targets.value() ?? []).length === 0) {
        <div class="empty">{{ 'storage.empty' | transloco }}</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{{ 'storage.table.name' | transloco }}</th>
                <th>{{ 'storage.table.endpoint' | transloco }}</th>
                <th>{{ 'storage.table.bucket' | transloco }}</th>
                <th>{{ 'storage.table.region' | transloco }}</th>
              </tr>
            </thead>
            <tbody>
              @for (t of targets.value() ?? []; track t.id) {
                <tr>
                  <td>{{ t.name }}</td>
                  <td class="mono">{{ t.endpoint }}</td>
                  <td class="mono">{{ t.bucket }}</td>
                  <td>{{ t.region ?? '—' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
})
export class StoragePage {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);
  private readonly transloco = inject(TranslocoService);

  protected readonly targets = httpResource<StorageTarget[]>(() => '/api/admin/storage-targets');
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected form: CreateStorageTargetRequest = { name: '', endpoint: '', bucket: '', region: null };

  protected create(): void {
    this.saving.set(true);
    this.error.set(null);
    const request = { ...this.form, region: this.form.region || null };
    this.api.createStorageTarget(request, new HttpContext().set(SILENT_ERRORS, true)).subscribe({
      next: (t) => {
        this.toasts.success(this.transloco.translate('storage.created', { name: t.name }));
        this.form = { name: '', endpoint: '', bucket: '', region: null };
        this.saving.set(false);
        this.targets.reload();
      },
      error: (e) => {
        this.error.set(problemMessage(e));
        this.saving.set(false);
      },
    });
  }
}
