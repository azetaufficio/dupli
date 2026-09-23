import { HttpContext, httpResource } from '@angular/common/http';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { catchError, debounceTime, distinctUntilChanged, of, switchMap } from 'rxjs';
import { ApiService } from '../core/api.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import {
  Agent,
  BackupSource,
  CronPreview,
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
  host: string;
  port: number;
  username: string;
  passwordSecret: string;
  excludeDatabases: string;
  includeGlobals: boolean;
  binDirectory: string;
}

const lines = (text: string): string[] =>
  text
    .split('\n')
    .map((l) => l.trim())
    .filter((l) => l.length > 0);

function newSource(type: SourceForm['type'], index: number): SourceForm {
  return {
    type,
    sourceId: type === 'directory' ? `files${index}` : `pg${index}`,
    paths: '',
    excludes: '',
    host: 'localhost',
    port: 5432,
    username: 'postgres',
    passwordSecret: 'pg-main',
    excludeDatabases: '',
    includeGlobals: true,
    binDirectory: '',
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
        host: source.host,
        port: source.port,
        username: source.username,
        passwordSecret: source.passwordSecret,
        excludeDatabases: source.excludeDatabases.join('\n'),
        includeGlobals: source.includeGlobals,
        binDirectory: source.binDirectory ?? '',
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
        host: s.host.trim(),
        port: Number(s.port),
        username: s.username.trim(),
        passwordSecret: s.passwordSecret.trim(),
        excludeDatabases: lines(s.excludeDatabases),
        includeGlobals: s.includeGlobals,
        binDirectory: s.binDirectory.trim() || null,
      };
}

function supportedTimeZones(): string[] {
  const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
  return intl.supportedValuesOf?.('timeZone') ?? ['Europe/Rome', 'UTC'];
}

@Component({
  selector: 'app-policy-editor',
  imports: [FormsModule, RouterLink],
  template: `
    <div class="page-header">
      <div>
        <p class="muted">
          <a routerLink="/agents">Agents</a> /
          @if (agent.value(); as a) {
            <a [routerLink]="['/agents', a.id]">{{ a.name }}</a> /
          }
        </p>
        <h1>{{ id() ? 'Edit policy' : 'New policy' }}</h1>
      </div>
      @if (id()) {
        <div class="toolbar">
          <button type="button" class="btn danger" (click)="remove()">Delete policy</button>
        </div>
      }
    </div>

    @if (ready()) {
      <form class="form" (ngSubmit)="save()" #f="ngForm">
        <section class="card form">
          <h2>General</h2>
          <div class="form-row">
            <label class="field">Name <input name="name" [(ngModel)]="name" required /></label>
            <label class="field">
              Storage target
              <input [value]="storageLabel()" disabled />
              <span class="hint"
                >Per agent: every policy of this agent goes to the same repository.</span
              >
            </label>
          </div>
          <label class="check"
            ><input type="checkbox" name="enabled" [(ngModel)]="enabled" /> Enabled
            (scheduled)</label
          >
        </section>

        <section class="card form">
          <h2>Schedule</h2>
          <div class="form-row">
            <label class="field">
              Cron
              <span class="hint"
                >5 fields: minute hour day-of-month month day-of-week. E.g. <code>0 2 * * *</code> =
                every day at 02:00.</span
              >
              <input
                name="cron"
                class="mono"
                [ngModel]="cron()"
                (ngModelChange)="cron.set($event)"
                required
              />
            </label>
            <label class="field">
              Time zone
              <select name="timeZone" [ngModel]="timeZone()" (ngModelChange)="timeZone.set($event)">
                @for (tz of timeZones; track tz) {
                  <option [value]="tz">{{ tz }}</option>
                }
              </select>
            </label>
          </div>
          @if (preview(); as p) {
            @if (p.valid) {
              <div class="muted">Next runs: {{ formatNext(p) }}</div>
            } @else {
              <div class="error-box">{{ p.error }}</div>
            }
          }
        </section>

        <section class="card form">
          <h2>Retention</h2>
          <div class="form-row">
            <label class="field"
              >Daily snapshots
              <input type="number" min="0" name="kd" [(ngModel)]="keepDaily" required
            /></label>
            <label class="field"
              >Weekly snapshots
              <input type="number" min="0" name="kw" [(ngModel)]="keepWeekly" required
            /></label>
            <label class="field"
              >Monthly snapshots
              <input type="number" min="0" name="km" [(ngModel)]="keepMonthly" required
            /></label>
          </div>
          <span class="muted"
            >Applied by the weekly retention job (restic forget --keep-daily/weekly/monthly, then
            prune).</span
          >
        </section>

        <section class="card form">
          <div class="page-header" style="margin: 0">
            <h2 style="margin: 0">Sources</h2>
            <div class="toolbar">
              <button type="button" class="btn small" (click)="addSource('directory')">
                + Folders
              </button>
              <button type="button" class="btn small" (click)="addSource('postgres')">
                + PostgreSQL
              </button>
            </div>
          </div>
          @if (sources().length === 0) {
            <div class="empty">Add at least one source.</div>
          }
          @for (s of sources(); track $index; let i = $index) {
            <div class="card" style="margin: 0; box-shadow: none">
              <div class="page-header" style="margin-bottom: 0.75rem">
                <h3 style="margin: 0">
                  {{ s.type === 'directory' ? 'Folders' : 'PostgreSQL instance' }}
                </h3>
                <button type="button" class="btn small danger" (click)="removeSource(i)">
                  Remove
                </button>
              </div>
              <div class="form">
                <label class="field">
                  Source id
                  <span class="hint"
                    >Stable key used in the restic <code>source=</code> tag. Changing it starts a
                    new snapshot history.</span
                  >
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
                      Paths <span class="hint">One per line, e.g. <code>D:\\Data</code></span>
                      <textarea [name]="'paths' + i" [(ngModel)]="s.paths" required></textarea>
                    </label>
                    <label class="field">
                      Excludes
                      <span class="hint">One restic exclude pattern per line (optional)</span>
                      <textarea [name]="'ex' + i" [(ngModel)]="s.excludes"></textarea>
                    </label>
                  </div>
                } @else {
                  <div class="form-row">
                    <label class="field"
                      >Host <input [name]="'host' + i" [(ngModel)]="s.host" required
                    /></label>
                    <label class="field"
                      >Port <input type="number" [name]="'port' + i" [(ngModel)]="s.port" required
                    /></label>
                    <label class="field"
                      >Username <input [name]="'user' + i" [(ngModel)]="s.username" required
                    /></label>
                    <label class="field">
                      Password secret name
                      <input [name]="'pw' + i" [(ngModel)]="s.passwordSecret" required />
                      <span class="hint"
                        >Not the password: the name of the secret stored on the VM with
                        <code>dupli-agent secret set {{ s.passwordSecret || '&lt;name&gt;' }}</code
                        >.</span
                      >
                    </label>
                  </div>
                  <div class="form-row">
                    <label class="field">
                      Excluded databases
                      <span class="hint"
                        >One per line. Templates and <code>postgres</code> are always skipped.</span
                      >
                      <textarea [name]="'exdb' + i" [(ngModel)]="s.excludeDatabases"></textarea>
                    </label>
                    <label class="field">
                      pg_dump directory
                      <span class="hint"
                        >Optional. Auto-detected from the registry when empty.</span
                      >
                      <input
                        [name]="'bin' + i"
                        [(ngModel)]="s.binDirectory"
                        placeholder="C:\\Program Files\\PostgreSQL\\18\\bin"
                      />
                    </label>
                  </div>
                  <label class="check">
                    <input type="checkbox" [name]="'glob' + i" [(ngModel)]="s.includeGlobals" />
                    Include roles and tablespaces (<code>pg_dumpall --globals-only</code>)
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
          <button
            type="submit"
            class="btn primary"
            [disabled]="f.invalid || sources().length === 0 || saving()"
          >
            {{ id() ? 'Save changes' : 'Create policy' }}
          </button>
          <a class="btn" [routerLink]="['/agents', agentIdResolved()]">Cancel</a>
        </div>
      </form>
    } @else {
      <p class="muted">Loading…</p>
    }
  `,
})
export class PolicyEditorPage {
  /** Set when editing (/policies/:id). */
  readonly id = input<string>();
  /** Set when creating (/agents/:agentId/policies/new). */
  readonly agentId = input<string>();

  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

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
    this.sources.update((list) => [
      ...list,
      newSource(type, list.filter((s) => s.type === type).length + 1),
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
        this.toasts.success(`Policy "${policy.name}" saved`);
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
      'Delete policy?',
      'The policy and its run history are deleted. Existing snapshots stay in the repository until retention removes them.',
      { confirmLabel: 'Delete', danger: true },
    );
    if (!ok) return;
    const agentId = this.agentIdResolved();
    this.api.deletePolicy(id).subscribe(() => {
      this.toasts.success('Policy deleted');
      this.router.navigate(['/agents', agentId]);
    });
  }
}
