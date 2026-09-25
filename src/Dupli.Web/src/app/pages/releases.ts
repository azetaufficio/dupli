import { HttpContext, httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
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
  imports: [Badge, DateTimePipe, FormsModule, TranslocoModule],
  template: `
    <div class="page-header">
      <div>
        <h1>{{ 'releases.title' | transloco }}</h1>
        <p class="muted">{{ 'releases.subtitle' | transloco }}</p>
      </div>
    </div>

    @if (auth.isOwner()) {
      <section class="card">
        <h2>{{ 'releases.importForm.title' | transloco }}</h2>
        <p class="muted" [innerHTML]="'releases.importForm.description' | transloco"></p>
        <form class="form" (ngSubmit)="importFromGitHub()" #imp="ngForm">
          <div class="form-row">
            <label class="field"
              >{{ 'releases.importForm.versionLabel' | transloco }}
              <input
                name="importVersion"
                [(ngModel)]="importForm.version"
                required
                placeholder="0.2.0"
              />
            </label>
            <label class="field">
              {{ 'releases.importForm.channelLabel' | transloco }}
              <select name="importChannel" [(ngModel)]="importForm.channel">
                @for (c of channels; track c) {
                  <option [value]="c">{{ c }}</option>
                }
              </select>
            </label>
            <label class="check">
              <input
                type="checkbox"
                name="importMakeCurrent"
                [(ngModel)]="importForm.makeCurrent"
              />
              {{ 'releases.makeCurrentCheckbox' | transloco }}
            </label>
          </div>
          @if (importError()) {
            <div class="error-box">{{ importError() }}</div>
          }
          <div class="toolbar">
            <button type="submit" class="btn primary" [disabled]="imp.invalid || importing()">
              {{ 'releases.importForm.submit' | transloco }}
            </button>
          </div>
        </form>
      </section>

      <section class="card">
        <h2>{{ 'releases.manualForm.title' | transloco }}</h2>
        <form class="form" (ngSubmit)="registerManually()" #man="ngForm">
          <div class="form-row">
            <label class="field">
              {{ 'releases.manualForm.productLabel' | transloco }}
              <select name="manualProduct" [(ngModel)]="manualForm.product">
                <option value="agent">agent</option>
                <option value="restic">restic</option>
              </select>
            </label>
            <label class="field"
              >{{ 'releases.manualForm.versionLabel' | transloco }}
              <input
                name="manualVersion"
                [(ngModel)]="manualForm.version"
                required
                placeholder="0.2.0"
              />
            </label>
            <label class="field">
              {{ 'releases.manualForm.platformLabel' | transloco }}
              <select name="manualPlatform" [(ngModel)]="manualForm.platform">
                @for (p of platforms; track p) {
                  <option [value]="p">{{ p }}</option>
                }
              </select>
            </label>
            @if (manualForm.product === 'agent') {
              <label class="field">
                {{ 'releases.manualForm.channelLabel' | transloco }}
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
              >{{ 'releases.manualForm.sourceUrlLabel' | transloco }}
              <input
                name="manualSourceUrl"
                type="url"
                [(ngModel)]="manualForm.sourceUrl"
                required
                placeholder="https://…"
              />
            </label>
            <label class="field"
              >{{ 'releases.manualForm.sha256Label' | transloco }}
              <input
                name="manualSha256"
                class="mono"
                [(ngModel)]="manualForm.sha256"
                required
                pattern="[0-9a-fA-F]{64}"
              />
            </label>
            <label class="check">
              <input
                type="checkbox"
                name="manualMakeCurrent"
                [(ngModel)]="manualForm.makeCurrent"
              />
              {{ 'releases.makeCurrentCheckbox' | transloco }}
            </label>
          </div>
          @if (manualError()) {
            <div class="error-box">{{ manualError() }}</div>
          }
          <div class="toolbar">
            <button type="submit" class="btn primary" [disabled]="man.invalid || registering()">
              {{ 'releases.manualForm.submit' | transloco }}
            </button>
          </div>
        </form>
      </section>
    }

    <section class="card flush">
      <div class="card-header"><h2>{{ 'releases.agentSection.title' | transloco }}</h2></div>
      @if ((agentReleases.value() ?? []).length === 0) {
        <div class="empty">{{ 'releases.agentSection.empty' | transloco }}</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{{ 'releases.table.version' | transloco }}</th>
                <th>{{ 'releases.table.platform' | transloco }}</th>
                <th>{{ 'releases.table.channel' | transloco }}</th>
                <th>{{ 'releases.table.current' | transloco }}</th>
                <th>{{ 'releases.table.sha256' | transloco }}</th>
                <th>{{ 'releases.table.sourceUrl' | transloco }}</th>
                <th>{{ 'releases.table.created' | transloco }}</th>
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
                      <app-badge value="Active" [text]="'releases.currentBadge' | transloco" />
                    } @else {
                      <span class="muted">—</span>
                    }
                  </td>
                  <td class="mono" [title]="r.sha256">{{ short(r.sha256) }}</td>
                  <td class="mono" style="word-break: break-all">{{ r.sourceUrl }}</td>
                  <td class="nowrap">{{ r.createdAt | datetime }}</td>
                  <td class="num">
                    @if (!r.isCurrent && auth.isOwner()) {
                      <button type="button" class="btn small" (click)="makeCurrent(r)">
                        {{ 'releases.makeCurrentButton' | transloco }}
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
      <div class="card-header"><h2>{{ 'releases.resticSection.title' | transloco }}</h2></div>
      @if ((resticReleases.value() ?? []).length === 0) {
        <div class="empty">{{ 'releases.resticSection.empty' | transloco }}</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{{ 'releases.table.version' | transloco }}</th>
                <th>{{ 'releases.table.platform' | transloco }}</th>
                <th>{{ 'releases.table.channel' | transloco }}</th>
                <th>{{ 'releases.table.current' | transloco }}</th>
                <th>{{ 'releases.table.sha256' | transloco }}</th>
                <th>{{ 'releases.table.sourceUrl' | transloco }}</th>
                <th>{{ 'releases.table.created' | transloco }}</th>
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
                      <app-badge value="Active" [text]="'releases.currentBadge' | transloco" />
                    } @else {
                      <span class="muted">—</span>
                    }
                  </td>
                  <td class="mono" [title]="r.sha256">{{ short(r.sha256) }}</td>
                  <td class="mono" style="word-break: break-all">{{ r.sourceUrl }}</td>
                  <td class="nowrap">{{ r.createdAt | datetime }}</td>
                  <td class="num">
                    @if (!r.isCurrent && auth.isOwner()) {
                      <button type="button" class="btn small" (click)="makeCurrent(r)">
                        {{ 'releases.makeCurrentButton' | transloco }}
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
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly transloco = inject(TranslocoService);

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
          this.toasts.success(
            this.transloco.translate('releases.toasts.imported', { count: releases.length }),
          );
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
        this.toasts.success(
          this.transloco.translate('releases.toasts.registered', {
            version: release.version,
            platform: release.platform,
          }),
        );
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
    const message = r.channel
      ? this.transloco.translate('releases.confirmMakeCurrent.messageWithChannel', {
          channel: r.channel,
        })
      : this.transloco.translate('releases.confirmMakeCurrent.messageBase');
    const ok = await this.confirm.ask(
      this.transloco.translate('releases.confirmMakeCurrent.title', {
        version: r.version,
        platform: r.platform,
      }),
      message,
      { confirmLabel: this.transloco.translate('releases.makeCurrentButton') },
    );
    if (!ok) return;
    this.api.makeReleaseCurrent(r.id).subscribe(() => {
      this.toasts.success(
        this.transloco.translate('releases.toasts.nowCurrent', {
          version: r.version,
          platform: r.platform,
        }),
      );
      this.agentReleases.reload();
      this.resticReleases.reload();
    });
  }

  protected short(sha256: string): string {
    return sha256.slice(0, 12) + '…';
  }
}
