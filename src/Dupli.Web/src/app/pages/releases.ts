import { HttpContext, httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import {
  AGENT_CHANNELS,
  AGENT_PLATFORMS,
  AgentChannel,
  AgentPlatform,
  Release,
  ReleaseProduct,
} from '../core/models';
import { ToastService } from '../core/toast.service';
import { Badge } from '../shared/badge';
import { ConfirmService } from '../shared/confirm';
import { DateTimePipe } from '../shared/format';

interface ManualForm {
  product: ReleaseProduct;
  version: string;
  platform: AgentPlatform;
  channel: AgentChannel;
  sourceUrl: string;
  sha256: string;
  makeCurrent: boolean;
}

interface ImportForm {
  version: string;
  channel: AgentChannel;
  makeCurrent: boolean;
}

function emptyManualForm(): ManualForm {
  return {
    product: 'agent',
    version: '',
    platform: 'windows_amd64',
    channel: 'stable',
    sourceUrl: '',
    sha256: '',
    makeCurrent: true,
  };
}

function emptyImportForm(): ImportForm {
  return { version: '', channel: 'stable', makeCurrent: true };
}

@Component({
  selector: 'app-releases',
  imports: [Badge, DateTimePipe, FormsModule],
  template: `
    <div class="page-header">
      <div>
        <h1>Releases</h1>
        <p class="muted">
          Agent and restic versions mirrored by the server and distributed via the heartbeat.
        </p>
      </div>
    </div>

    <section class="card">
      <h2>Import from GitHub</h2>
      <p class="muted">
        Registers the agent releases found on the <code>v&lt;version&gt;</code> tag of the
        configured repository (one per available platform).
      </p>
      <form class="form" (ngSubmit)="importFromGitHub()" #imp="ngForm">
        <div class="form-row">
          <label class="field"
            >Version
            <input
              name="importVersion"
              [(ngModel)]="importForm.version"
              required
              placeholder="0.2.0"
            />
          </label>
          <label class="field">
            Channel
            <select name="importChannel" [(ngModel)]="importForm.channel">
              @for (c of channels; track c) {
                <option [value]="c">{{ c }}</option>
              }
            </select>
          </label>
          <label class="check">
            <input type="checkbox" name="importMakeCurrent" [(ngModel)]="importForm.makeCurrent" />
            Make current
          </label>
        </div>
        @if (importError()) {
          <div class="error-box">{{ importError() }}</div>
        }
        <div class="toolbar">
          <button type="submit" class="btn primary" [disabled]="imp.invalid || importing()">
            Import
          </button>
        </div>
      </form>
    </section>

    <section class="card">
      <h2>Register manually</h2>
      <form class="form" (ngSubmit)="registerManually()" #man="ngForm">
        <div class="form-row">
          <label class="field">
            Product
            <select name="manualProduct" [(ngModel)]="manualForm.product">
              <option value="agent">agent</option>
              <option value="restic">restic</option>
            </select>
          </label>
          <label class="field"
            >Version
            <input
              name="manualVersion"
              [(ngModel)]="manualForm.version"
              required
              placeholder="0.2.0"
            />
          </label>
          <label class="field">
            Platform
            <select name="manualPlatform" [(ngModel)]="manualForm.platform">
              @for (p of platforms; track p) {
                <option [value]="p">{{ p }}</option>
              }
            </select>
          </label>
          @if (manualForm.product === 'agent') {
            <label class="field">
              Channel
              <select name="manualChannel" [(ngModel)]="manualForm.channel">
                @for (c of channels; track c) {
                  <option [value]="c">{{ c }}</option>
                }
              </select>
            </label>
          }
        </div>
        <div class="form-row">
          <label class="field"
            >Source URL
            <input
              name="manualSourceUrl"
              type="url"
              [(ngModel)]="manualForm.sourceUrl"
              required
              placeholder="https://…"
            />
          </label>
          <label class="field"
            >sha256
            <input
              name="manualSha256"
              class="mono"
              [(ngModel)]="manualForm.sha256"
              required
              pattern="[0-9a-fA-F]{64}"
            />
          </label>
          <label class="check">
            <input type="checkbox" name="manualMakeCurrent" [(ngModel)]="manualForm.makeCurrent" />
            Make current
          </label>
        </div>
        @if (manualError()) {
          <div class="error-box">{{ manualError() }}</div>
        }
        <div class="toolbar">
          <button type="submit" class="btn primary" [disabled]="man.invalid || registering()">
            Register
          </button>
        </div>
      </form>
    </section>

    <section class="card flush">
      <div class="card-header"><h2>Agent</h2></div>
      @if ((agentReleases.value() ?? []).length === 0) {
        <div class="empty">No agent releases registered.</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Version</th>
                <th>Platform</th>
                <th>Channel</th>
                <th>Current</th>
                <th>sha256</th>
                <th>Source URL</th>
                <th>Created</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (r of agentReleases.value() ?? []; track r.id) {
                <tr>
                  <td class="mono">{{ r.version }}</td>
                  <td class="mono">{{ r.platform }}</td>
                  <td class="mono">{{ r.channel ?? '—' }}</td>
                  <td>
                    @if (r.isCurrent) {
                      <app-badge value="Active" text="Current" />
                    } @else {
                      <span class="muted">—</span>
                    }
                  </td>
                  <td class="mono" [title]="r.sha256">{{ short(r.sha256) }}</td>
                  <td class="mono" style="word-break: break-all">{{ r.sourceUrl }}</td>
                  <td class="nowrap">{{ r.createdAt | datetime }}</td>
                  <td class="num">
                    @if (!r.isCurrent) {
                      <button type="button" class="btn small" (click)="makeCurrent(r)">
                        Make current
                      </button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>

    <section class="card flush">
      <div class="card-header"><h2>restic</h2></div>
      @if ((resticReleases.value() ?? []).length === 0) {
        <div class="empty">No restic releases registered.</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Version</th>
                <th>Platform</th>
                <th>Channel</th>
                <th>Current</th>
                <th>sha256</th>
                <th>Source URL</th>
                <th>Created</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (r of resticReleases.value() ?? []; track r.id) {
                <tr>
                  <td class="mono">{{ r.version }}</td>
                  <td class="mono">{{ r.platform }}</td>
                  <td class="mono">{{ r.channel ?? '—' }}</td>
                  <td>
                    @if (r.isCurrent) {
                      <app-badge value="Active" text="Current" />
                    } @else {
                      <span class="muted">—</span>
                    }
                  </td>
                  <td class="mono" [title]="r.sha256">{{ short(r.sha256) }}</td>
                  <td class="mono" style="word-break: break-all">{{ r.sourceUrl }}</td>
                  <td class="nowrap">{{ r.createdAt | datetime }}</td>
                  <td class="num">
                    @if (!r.isCurrent) {
                      <button type="button" class="btn small" (click)="makeCurrent(r)">
                        Make current
                      </button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
})
export class ReleasesPage {
  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  protected readonly channels = AGENT_CHANNELS;
  protected readonly platforms = AGENT_PLATFORMS;

  protected readonly agentReleases = httpResource<Release[]>(() => ({
    url: '/api/admin/releases',
    params: { product: 'agent' },
  }));
  protected readonly resticReleases = httpResource<Release[]>(() => ({
    url: '/api/admin/releases',
    params: { product: 'restic' },
  }));

  protected importForm: ImportForm = emptyImportForm();
  protected readonly importing = signal(false);
  protected readonly importError = signal<string | null>(null);

  protected manualForm: ManualForm = emptyManualForm();
  protected readonly registering = signal(false);
  protected readonly manualError = signal<string | null>(null);

  protected importFromGitHub(): void {
    this.importing.set(true);
    this.importError.set(null);
    this.api
      .importAgentRelease(
        {
          version: this.importForm.version.trim(),
          channel: this.importForm.channel,
          makeCurrent: this.importForm.makeCurrent,
        },
        new HttpContext().set(SILENT_ERRORS, true),
      )
      .subscribe({
        next: (releases) => {
          this.toasts.success(`Imported ${releases.length} release(s)`);
          this.importForm = emptyImportForm();
          this.importing.set(false);
          this.agentReleases.reload();
        },
        error: (e) => {
          this.importError.set(problemMessage(e));
          this.importing.set(false);
        },
      });
  }

  protected registerManually(): void {
    this.registering.set(true);
    this.manualError.set(null);
    const context = new HttpContext().set(SILENT_ERRORS, true);
    const call =
      this.manualForm.product === 'agent'
        ? this.api.createAgentRelease(
            {
              version: this.manualForm.version.trim(),
              platform: this.manualForm.platform,
              channel: this.manualForm.channel,
              sourceUrl: this.manualForm.sourceUrl.trim(),
              sha256: this.manualForm.sha256.trim(),
              makeCurrent: this.manualForm.makeCurrent,
            },
            context,
          )
        : this.api.createResticRelease(
            {
              version: this.manualForm.version.trim(),
              platform: this.manualForm.platform,
              sourceUrl: this.manualForm.sourceUrl.trim(),
              sha256: this.manualForm.sha256.trim(),
              makeCurrent: this.manualForm.makeCurrent,
            },
            context,
          );
    call.subscribe({
      next: (release) => {
        this.toasts.success(`Release ${release.version} (${release.platform}) registered`);
        const product = this.manualForm.product;
        this.manualForm = emptyManualForm();
        this.manualForm.product = product;
        this.registering.set(false);
        this.agentReleases.reload();
        this.resticReleases.reload();
      },
      error: (e) => {
        this.manualError.set(problemMessage(e));
        this.registering.set(false);
      },
    });
  }

  protected async makeCurrent(r: Release): Promise<void> {
    const ok = await this.confirm.ask(
      `Make ${r.version} (${r.platform}) current?`,
      'It will become the desired version for agents on this platform' +
        (r.channel ? ` and channel ${r.channel}.` : '.'),
      { confirmLabel: 'Make current' },
    );
    if (!ok) return;
    this.api.makeReleaseCurrent(r.id).subscribe(() => {
      this.toasts.success(`${r.version} (${r.platform}) is now current`);
      this.agentReleases.reload();
      this.resticReleases.reload();
    });
  }

  protected short(sha256: string): string {
    return sha256.slice(0, 12) + '…';
  }
}
