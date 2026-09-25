import { HttpContext, httpResource } from '@angular/common/http';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';
import { catchError, debounceTime, distinctUntilChanged, of, switchMap } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import {
  Agent,
  BackupSource,
  CronPreview,
  DatabaseSelection,
  PgConnection,
  Policy,
  PolicyRequest,
  StorageTarget,
} from '../core/models';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';

/** Editable source: list fields are edited as one-entry-per-line text. */
interface SourceForm {
  type: 'directory' | 'postgres';
  sourceId: string;
  paths: string;
  excludes: string;
  connectionId: string;
  databaseSelection: DatabaseSelection;
  excludeDatabases: string;
  includeDatabases: string;
  includeGlobals: boolean;
}

const lines = (text: string): string[] =>
  text
    .split('\n')
    .map((l) => l.trim())
    .filter((l) => l.length > 0);

function newSource(type: SourceForm['type'], index: number, connectionId = ''): SourceForm {
  return {
    type,
    sourceId: type === 'directory' ? `files${index}` : `pg${index}`,
    paths: '',
    excludes: '',
    connectionId,
    databaseSelection: 'AllExcept',
    excludeDatabases: '',
    includeDatabases: '',
    includeGlobals: true,
  };
}

function toForm(source: BackupSource): SourceForm {
  const base = newSource(source.type, 0);
  return source.type === 'directory'
    ? {
        ...base,
        sourceId: source.sourceId,
        paths: source.paths.join('\n'),
        excludes: source.excludes.join('\n'),
      }
    : {
        ...base,
        sourceId: source.sourceId,
        connectionId: source.connectionId,
        databaseSelection: source.databaseSelection ?? 'AllExcept',
        excludeDatabases: source.excludeDatabases.join('\n'),
        includeDatabases: (source.includeDatabases ?? []).join('\n'),
        includeGlobals: source.includeGlobals,
      };
}

/** "type" first: it is the polymorphic discriminator. */
function toDto(s: SourceForm): BackupSource {
  return s.type === 'directory'
    ? {
        type: 'directory',
        sourceId: s.sourceId.trim(),
        paths: lines(s.paths),
        excludes: lines(s.excludes),
      }
    : {
        type: 'postgres',
        sourceId: s.sourceId.trim(),
        connectionId: s.connectionId,
        databaseSelection: s.databaseSelection,
        excludeDatabases: s.databaseSelection === 'AllExcept' ? lines(s.excludeDatabases) : [],
        includeDatabases: s.databaseSelection === 'Only' ? lines(s.includeDatabases) : [],
        includeGlobals: s.includeGlobals,
      };
}

function supportedTimeZones(): string[] {
  const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
  return intl.supportedValuesOf?.('timeZone') ?? ['Europe/Rome', 'UTC'];
}

@Component({
  selector: 'app-policy-editor',
  imports: [FormsModule, RouterLink, TranslocoModule],
  template: `
    <div class="page-header">
      <div>
        <p class="muted">
          <a routerLink="/agents">{{ 'policyEditor.breadcrumbAgents' | transloco }}</a> /
          @if (agent.value(); as a) {
            <a [routerLink]="['/agents', a.id]">{{ a.name }}</a> /
          }
        </p>
        <h1>{{ (id() ? 'policyEditor.title.edit' : 'policyEditor.title.new') | transloco }}</h1>
      </div>
      @if (id() && auth.canOperate()) {
        <div class="toolbar">
          <button type="button" class="btn danger" (click)="remove()">
            {{ 'policyEditor.deletePolicy' | transloco }}
          </button>
        </div>
      }
    </div>

    @if (ready()) {
      <form class="form" (ngSubmit)="save()" #f="ngForm">
        <section class="card form">
          <h2>{{ 'policyEditor.general.title' | transloco }}</h2>
          <div class="form-row">
            <label class="field"
              >{{ 'policyEditor.general.nameLabel' | transloco }}
              <input name="name" [(ngModel)]="name" required
            /></label>
            <label class="field">
              {{ 'policyEditor.general.storageTargetLabel' | transloco }}
              <input [value]="storageLabel()" disabled />
              <span class="hint">{{ 'policyEditor.general.storageTargetHint' | transloco }}</span>
            </label>
          </div>
          <label class="check"
            ><input type="checkbox" name="enabled" [(ngModel)]="enabled" />
            {{ 'policyEditor.general.enabledLabel' | transloco }}</label
          >
        </section>

        <section class="card form">
          <h2>{{ 'policyEditor.schedule.title' | transloco }}</h2>
          <div class="form-row">
            <label class="field">
              {{ 'policyEditor.schedule.cronLabel' | transloco }}
              <span class="hint" [innerHTML]="'policyEditor.schedule.cronHint' | transloco"></span>
              <input
                name="cron"
                class="mono"
                [ngModel]="cron()"
                (ngModelChange)="cron.set($event)"
                required
              />
            </label>
            <label class="field">
              {{ 'policyEditor.schedule.timeZoneLabel' | transloco }}
              <select name="timeZone" [ngModel]="timeZone()" (ngModelChange)="timeZone.set($event)">
                @for (tz of timeZones; track tz) {
                  <option [value]="tz">{{ tz }}</option>
                }
              </select>
            </label>
          </div>
          @if (preview(); as p) {
            @if (p.valid) {
              <div class="muted">
                {{ 'policyEditor.schedule.nextRuns' | transloco: { next: formatNext(p) } }}
              </div>
            } @else {
              <div class="error-box">{{ p.error }}</div>
            }
          }
        </section>

        <section class="card form">
          <h2>{{ 'policyEditor.retention.title' | transloco }}</h2>
          <div class="form-row">
            <label class="field"
              >{{ 'policyEditor.retention.dailyLabel' | transloco }}
              <input type="number" min="0" name="kd" [(ngModel)]="keepDaily" required
            /></label>
            <label class="field"
              >{{ 'policyEditor.retention.weeklyLabel' | transloco }}
              <input type="number" min="0" name="kw" [(ngModel)]="keepWeekly" required
            /></label>
            <label class="field"
              >{{ 'policyEditor.retention.monthlyLabel' | transloco }}
              <input type="number" min="0" name="km" [(ngModel)]="keepMonthly" required
            /></label>
          </div>
          <span class="muted">{{ 'policyEditor.retention.hint' | transloco }}</span>
        </section>

        <section class="card form">
          <div class="page-header" style="margin: 0">
            <h2 style="margin: 0">{{ 'policyEditor.sources.title' | transloco }}</h2>
            <div class="toolbar">
              <button type="button" class="btn small" (click)="addSource('directory')">
                {{ 'policyEditor.sources.addFolders' | transloco }}
              </button>
              <button type="button" class="btn small" (click)="addSource('postgres')">
                {{ 'policyEditor.sources.addPostgres' | transloco }}
              </button>
            </div>
          </div>
          @if (sources().length === 0) {
            <div class="empty">{{ 'policyEditor.sources.empty' | transloco }}</div>
          }
          @for (s of sources(); track $index; let i = $index) {
            <div class="card" style="margin: 0; box-shadow: none">
              <div class="page-header" style="margin-bottom: 0.75rem">
                <h3 style="margin: 0">
                  {{
                    (s.type === 'directory'
                      ? 'policyEditor.sources.folderType'
                      : 'policyEditor.sources.postgresType'
                    ) | transloco
                  }}
                </h3>
                <button type="button" class="btn small danger" (click)="removeSource(i)">
                  {{ 'policyEditor.sources.remove' | transloco }}
                </button>
              </div>
              <div class="form">
                <label class="field">
                  {{ 'policyEditor.sources.sourceIdLabel' | transloco }}
                  <span
                    class="hint"
                    [innerHTML]="'policyEditor.sources.sourceIdHint' | transloco"
                  ></span>
                  <input
                    [name]="'sid' + i"
                    [(ngModel)]="s.sourceId"
                    required
                    pattern="[A-Za-z0-9_.\\-]+"
                  />
                </label>
                @if (s.type === 'directory') {
                  <div class="form-row">
                    <label class="field">
                      {{ 'policyEditor.sources.pathsLabel' | transloco }}
                      <span class="hint" [innerHTML]="'policyEditor.sources.pathsHint' | transloco"></span>
                      <textarea [name]="'paths' + i" [(ngModel)]="s.paths" required></textarea>
                    </label>
                    <label class="field">
                      {{ 'policyEditor.sources.excludesLabel' | transloco }}
                      <span class="hint">{{ 'policyEditor.sources.excludesHint' | transloco }}</span>
                      <textarea [name]="'ex' + i" [(ngModel)]="s.excludes"></textarea>
                    </label>
                  </div>
                } @else {
                  @if (connections().length === 0) {
                    <div class="hint">
                      {{ 'policyEditor.sources.postgres.noConnectionsPrefix' | transloco }}
                      <a [routerLink]="['/agents', agentIdResolved()]">{{
                        'policyEditor.sources.postgres.connectionsTabLink' | transloco
                      }}</a
                      >{{ 'policyEditor.sources.postgres.noConnectionsSuffix' | transloco }}
                    </div>
                  } @else {
                    <label class="field">
                      {{ 'policyEditor.sources.postgres.connectionLabel' | transloco }}
                      <select [name]="'conn' + i" [(ngModel)]="s.connectionId" required>
                        <option value="" disabled>
                          {{ 'policyEditor.sources.postgres.selectConnectionPlaceholder' | transloco }}
                        </option>
                        @for (c of connections(); track c.id) {
                          <option [value]="c.id">{{ c.name }}</option>
                        }
                      </select>
                      <span class="hint"
                        >{{ 'policyEditor.sources.postgres.connectionHintPrefix' | transloco }}
                        <a [routerLink]="['/agents', agentIdResolved()]">{{
                          'policyEditor.sources.postgres.connectionsLink' | transloco
                        }}</a
                        >.</span
                      >
                    </label>
                  }
                  <div class="form-row">
                    <div class="field">
                      {{ 'policyEditor.sources.postgres.databasesLabel' | transloco }}
                      <label class="check">
                        <input
                          type="radio"
                          [name]="'dbsel' + i"
                          value="AllExcept"
                          [(ngModel)]="s.databaseSelection"
                        />
                        {{ 'policyEditor.sources.postgres.allExceptLabel' | transloco }}
                      </label>
                      <label class="check">
                        <input
                          type="radio"
                          [name]="'dbsel' + i"
                          value="Only"
                          [(ngModel)]="s.databaseSelection"
                        />
                        {{ 'policyEditor.sources.postgres.onlyListedLabel' | transloco }}
                      </label>
                      @if (s.databaseSelection === 'AllExcept') {
                        <span
                          class="hint"
                          [innerHTML]="'policyEditor.sources.postgres.excludeHint' | transloco"
                        ></span>
                        <textarea
                          [name]="'exdb' + i"
                          [(ngModel)]="s.excludeDatabases"
                          [attr.aria-label]="
                            'policyEditor.sources.postgres.excludedAriaLabel' | transloco
                          "
                        ></textarea>
                      } @else {
                        <span
                          class="hint"
                          [innerHTML]="'policyEditor.sources.postgres.includeHint' | transloco"
                        ></span>
                        <textarea
                          [name]="'incdb' + i"
                          [(ngModel)]="s.includeDatabases"
                          [attr.aria-label]="
                            'policyEditor.sources.postgres.includedAriaLabel' | transloco
                          "
                          required
                        ></textarea>
                      }
                    </div>
                  </div>
                  <label class="check">
                    <input type="checkbox" [name]="'glob' + i" [(ngModel)]="s.includeGlobals" />
                    <span
                      [innerHTML]="'policyEditor.sources.postgres.includeGlobalsLabel' | transloco"
                    ></span>
                  </label>
                }
              </div>
            </div>
          }
        </section>

        @if (error()) {
          <div class="error-box">{{ error() }}</div>
        }
        <div class="toolbar">
          @if (auth.canOperate()) {
            <button
              type="submit"
              class="btn primary"
              [disabled]="f.invalid || sources().length === 0 || saving()"
            >
              {{
                (id() ? 'policyEditor.saveChanges' : 'policyEditor.createPolicy') | transloco
              }}
            </button>
          }
          <a class="btn" [routerLink]="['/agents', agentIdResolved()]">{{
            'common.cancel' | transloco
          }}</a>
        </div>
      </form>
    } @else {
      <p class="muted">{{ 'common.loading' | transloco }}</p>
    }
  `,
})
export class PolicyEditorPage {
  protected readonly auth = inject(AuthService);
  /** Set when editing (/policies/:id). */
  readonly id = input<string>();
  /** Set when creating (/agents/:agentId/policies/new). */
  readonly agentId = input<string>();

  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly transloco = inject(TranslocoService);

  protected readonly timeZones = supportedTimeZones();

  protected readonly existing = httpResource<Policy>(() =>
    this.id() ? `/api/admin/policies/${this.id()}` : undefined,
  );
  protected readonly agentIdResolved = computed(
    () => this.agentId() ?? this.existing.value()?.agentId,
  );
  protected readonly agent = httpResource<Agent>(() =>
    this.agentIdResolved() ? `/api/admin/agents/${this.agentIdResolved()}` : undefined,
  );
  protected readonly storageTargets = httpResource<StorageTarget[]>(
    () => '/api/admin/storage-targets',
  );
  protected readonly storageLabel = computed(() => {
    const agent = this.agent.value();
    const target = this.storageTargets.value()?.find((s) => s.id === agent?.storageTargetId);
    return target && agent ? `${target.name}: ${target.bucket}/${agent.storagePrefix}` : '…';
  });
  private readonly connectionsResource = httpResource<PgConnection[]>(() =>
    this.agentIdResolved() ? `/api/admin/agents/${this.agentIdResolved()}/connections` : undefined,
  );
  protected readonly connections = computed(() => this.connectionsResource.value() ?? []);

  protected name = '';
  protected enabled = true;
  protected keepDaily = 7;
  protected keepWeekly = 4;
  protected keepMonthly = 12;
  protected readonly cron = signal('0 2 * * *');
  protected readonly timeZone = signal('Europe/Rome');
  protected readonly sources = signal<SourceForm[]>([]);

  protected readonly ready = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly preview = toSignal(
    toObservable(computed(() => ({ cron: this.cron().trim(), timeZone: this.timeZone() }))).pipe(
      debounceTime(300),
      distinctUntilChanged((a, b) => a.cron === b.cron && a.timeZone === b.timeZone),
      switchMap(({ cron, timeZone }) =>
        cron
          ? this.api
              .cronPreview(cron, timeZone, 3)
              .pipe(catchError(() => of<CronPreview | null>(null)))
          : of(null),
      ),
    ),
  );

  constructor() {
    effect(() => {
      if (this.ready()) return;
      if (!this.id()) {
        this.sources.set([newSource('directory', 1)]);
        this.ready.set(true);
        return;
      }
      const p = this.existing.value();
      if (!p) return;
      this.name = p.name;
      this.enabled = p.enabled;
      this.keepDaily = p.retention.keepDaily;
      this.keepWeekly = p.retention.keepWeekly;
      this.keepMonthly = p.retention.keepMonthly;
      this.cron.set(p.cron);
      this.timeZone.set(p.timeZone);
      this.sources.set(p.sources.map(toForm));
      this.ready.set(true);
    });
  }

  protected formatNext(p: CronPreview): string {
    return p.next
      .map((d) =>
        new Date(d).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }),
      )
      .join(' · ');
  }

  protected addSource(type: SourceForm['type']): void {
    const connectionId = type === 'postgres' ? (this.connections()[0]?.id ?? '') : '';
    this.sources.update((list) => [
      ...list,
      newSource(type, list.filter((s) => s.type === type).length + 1, connectionId),
    ]);
  }

  protected removeSource(index: number): void {
    this.sources.update((list) => list.filter((_, i) => i !== index));
  }

  protected save(): void {
    const request: PolicyRequest = {
      name: this.name.trim(),
      cron: this.cron().trim(),
      timeZone: this.timeZone(),
      enabled: this.enabled,
      retention: {
        keepDaily: Number(this.keepDaily),
        keepWeekly: Number(this.keepWeekly),
        keepMonthly: Number(this.keepMonthly),
      },
      sources: this.sources().map(toDto),
    };
    const context = new HttpContext().set(SILENT_ERRORS, true);
    const id = this.id();
    const call = id
      ? this.api.updatePolicy(id, request, context)
      : this.api.createPolicy(this.agentId()!, request, context);

    this.saving.set(true);
    this.error.set(null);
    call.subscribe({
      next: (policy) => {
        this.toasts.success(this.transloco.translate('policyEditor.toasts.saved', { name: policy.name }));
        this.router.navigate(['/agents', policy.agentId]);
      },
      error: (e) => {
        this.error.set(problemMessage(e));
        this.saving.set(false);
      },
    });
  }

  protected async remove(): Promise<void> {
    const id = this.id();
    if (!id) return;
    const ok = await this.confirm.ask(
      this.transloco.translate('policyEditor.confirmDelete.title'),
      this.transloco.translate('policyEditor.confirmDelete.message'),
      { confirmLabel: this.transloco.translate('common.delete'), danger: true },
    );
    if (!ok) return;
    const agentId = this.agentIdResolved();
    this.api.deletePolicy(id).subscribe(() => {
      this.toasts.success(this.transloco.translate('policyEditor.toasts.deleted'));
      this.router.navigate(['/agents', agentId]);
    });
  }
}
