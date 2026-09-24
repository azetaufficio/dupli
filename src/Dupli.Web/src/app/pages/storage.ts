import { HttpContext, httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import { CreateStorageTargetRequest, StorageTarget } from '../core/models';
import { ToastService } from '../core/toast.service';

@Component({
  selector: 'app-storage',
  imports: [FormsModule],
  template: `
    <div class="page-header">
      <div>
        <h1>Storage targets</h1>
        <p class="muted">
          S3 endpoint + bucket shared by agents. Each agent writes under its own prefix with its own
          key.
        </p>
      </div>
    </div>

    @if (auth.isOwner()) {
      <section class="card">
        <h2>New storage target</h2>
        <form class="form" (ngSubmit)="create()" #f="ngForm">
          <div class="form-row">
            <label class="field"
              >Name <input name="name" [(ngModel)]="form.name" required placeholder="wasabi-eu"
            /></label>
            <label class="field">
              Endpoint
              <input
                name="endpoint"
                type="url"
                [(ngModel)]="form.endpoint"
                required
                placeholder="https://s3.eu-central-1.wasabisys.com"
              />
            </label>
            <label class="field"
              >Bucket <input name="bucket" [(ngModel)]="form.bucket" required
            /></label>
            <label class="field"
              >Region <span class="hint">Optional</span
              ><input name="region" [(ngModel)]="form.region"
            /></label>
          </div>
          @if (error()) {
            <div class="error-box">{{ error() }}</div>
          }
          <div class="toolbar">
            <button type="submit" class="btn primary" [disabled]="f.invalid || saving()">
              Create
            </button>
            <span class="muted"
              >Enable bucket versioning and a lifecycle rule for noncurrent versions on the provider
              side.</span
            >
          </div>
        </form>
      </section>
    }

    <section class="card flush">
      @if ((targets.value() ?? []).length === 0) {
        <div class="empty">No storage targets.</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Endpoint</th>
                <th>Bucket</th>
                <th>Region</th>
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
        this.toasts.success(`Storage target ${t.name} created`);
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
