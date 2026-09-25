import { HttpContext, httpResource } from '@angular/common/http';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import { Job, PgConnection, Snapshot, SnapshotNode } from '../core/models';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from './confirm';
import { BytesPipe, DateTimePipe } from './format';

const GLOBALS = '_globals';

/** "/C/Data/x" → ["/C", "/C/Data", "/C/Data/x"]. */
function crumbs(path: string): { name: string; path: string }[] {
  const parts = path.split('/').filter((p) => p.length > 0);
  return parts.map((name, i) => ({ name, path: '/' + parts.slice(0, i + 1).join('/') }));
}

/**
 * Converts the raw OS path given to `restic backup` into restic's internal tree-path form.
 * Windows: "D:\Data\x" or "D:/Data/x" → "/D/Data/x" (drive letter as the first segment, forward slashes).
 * POSIX paths already match restic's tree form and pass through unchanged.
 */
export function toResticTreePath(rawPath: string): string {
  const slashed = rawPath.replace(/\\/g, '/');
  const drive = /^([A-Za-z]):\//.exec(slashed);
  return drive ? `/${drive[1]}${slashed.slice(2)}` : slashed;
}

/**
 * Snapshot list and browser of an agent's repository (read by the server), plus the restore form.
 * The restore itself is a job run by the agent, into an alternative directory.
 */
@Component({
  selector: 'app-snapshot-browser',
  imports: [FormsModule, BytesPipe, DateTimePipe, TranslocoModule],
  template: `
    @if (!selected()) {
      <div class="toolbar">
        <select
          [ngModel]="policyFilter()"
          (ngModelChange)="policyFilter.set($event)"
          [attr.aria-label]="'snapshotBrowser.policyAriaLabel' | transloco"
        >
          <option value="">{{ 'snapshotBrowser.allPolicies' | transloco }}</option>
          @for (p of policyOptions(); track p.id) {
            <option [value]="p.id">{{ p.name }}</option>
          }
        </select>
        <select
          [ngModel]="typeFilter()"
          (ngModelChange)="typeFilter.set($event)"
          [attr.aria-label]="'snapshotBrowser.typeAriaLabel' | transloco"
        >
          <option value="">{{ 'snapshotBrowser.allTypes' | transloco }}</option>
          <option value="dir">{{ 'snapshotBrowser.folders' | transloco }}</option>
          <option value="pg">PostgreSQL</option>
        </select>
        <button type="button" class="btn" (click)="load(true)" [disabled]="loading()">
          {{ 'snapshotBrowser.refresh' | transloco }}
        </button>
      </div>
      @if (loading() && snapshots().length === 0) {
        <p class="muted">{{ 'snapshotBrowser.readingRepository' | transloco }}</p>
      } @else if (loadError()) {
        <p class="error-box">{{ loadError() }}</p>
      } @else if (filtered().length === 0) {
        <div class="empty">{{ 'snapshotBrowser.empty' | transloco }}</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{{ 'snapshotBrowser.table.time' | transloco }}</th>
                <th>{{ 'snapshotBrowser.table.policy' | transloco }}</th>
                <th>{{ 'snapshotBrowser.table.source' | transloco }}</th>
                <th>{{ 'snapshotBrowser.table.content' | transloco }}</th>
                <th>{{ 'snapshotBrowser.table.snapshot' | transloco }}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (s of filtered(); track s.id) {
                <tr>
                  <td class="nowrap">{{ s.time | datetime }}</td>
                  <td>
                    {{
                      s.policyName ??
                        (s.policyId ? ('snapshotBrowser.deletedPolicy' | transloco) : '—')
                    }}
                  </td>
                  <td>{{ s.sourceId ?? '—' }}</td>
                  <td class="mono">
                    @if (s.type === 'pg') {
                      {{
                        s.database === globals
                          ? ('snapshotBrowser.rolesAndTablespaces' | transloco)
                          : ('snapshotBrowser.database' | transloco: { name: s.database })
                      }}
                    } @else {
                      {{ s.paths.join(', ') }}
                    }
                  </td>
                  <td class="mono">{{ s.shortId }}</td>
                  <td class="num">
                    @if (auth.canOperate()) {
                      <button type="button" class="btn small" (click)="open(s)">
                        {{
                          s.type === 'pg'
                            ? ('snapshotBrowser.restore' | transloco)
                            : ('snapshotBrowser.browse' | transloco)
                        }}
                      </button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    } @else {
      @let s = selected()!;
      <div class="toolbar">
        <button type="button" class="btn" (click)="close()">
          {{ 'snapshotBrowser.backToSnapshots' | transloco }}
        </button>
        <span class="muted"
          >{{ 'snapshotBrowser.snapshotLabel' | transloco }}
          <span class="mono">{{ s.shortId }}</span> — {{ s.time | datetime }}
          @if (s.policyName) {
            — {{ s.policyName }} / {{ s.sourceId }}
          }
        </span>
      </div>

      <nav class="crumbs" [attr.aria-label]="'snapshotBrowser.pathAriaLabel' | transloco">
        <button type="button" class="link" (click)="browse('/')">/</button>
        @for (c of pathCrumbs(); track c.path) {
          @if (!$first) {
            <span class="muted">/</span>
          }
          <button type="button" class="link" (click)="browse(c.path)">{{ c.name }}</button>
        }
      </nav>

      @if (treeLoading()) {
        <p class="muted">{{ 'common.loading' | transloco }}</p>
      } @else if (treeError()) {
        <p class="error-box">{{ treeError() }}</p>
      } @else if (nodes().length === 0) {
        <div class="empty">{{ 'snapshotBrowser.emptyDirectory' | transloco }}</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th class="check-col">
                  <input
                    type="checkbox"
                    [attr.aria-label]="'snapshotBrowser.selectAll' | transloco"
                    [checked]="allSelected()"
                    (change)="toggleAll()"
                  />
                </th>
                <th>{{ 'snapshotBrowser.table.name' | transloco }}</th>
                <th class="num">{{ 'snapshotBrowser.table.size' | transloco }}</th>
                <th>{{ 'snapshotBrowser.table.modified' | transloco }}</th>
              </tr>
            </thead>
            <tbody>
              @for (n of nodes(); track n.path) {
                <tr>
                  <td class="check-col">
                    <input
                      type="checkbox"
                      [attr.aria-label]="
                        'snapshotBrowser.selectNodeAriaLabel' | transloco: { name: n.name }
                      "
                      [checked]="isSelected(n.path)"
                      [disabled]="coveredByParent(n.path)"
                      (change)="toggle(n.path)"
                    />
                  </td>
                  <td class="mono">
                    @if (n.type === 'Directory') {
                      <button type="button" class="link" (click)="browse(n.path)">
                        {{ n.name }}/
                      </button>
                    } @else {
                      {{ n.name }}
                    }
                  </td>
                  <td class="num">{{ n.type === 'File' ? (n.size | bytes) : '' }}</td>
                  <td class="nowrap">{{ n.modifiedAt | datetime }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      <section class="card form restore-form">
        <h2>{{ 'snapshotBrowser.restore' | transloco }}</h2>
        <p class="muted">
          @if (selection().length === 0) {
            {{ 'snapshotBrowser.wholeSnapshot' | transloco }}
          } @else {
            {{ 'snapshotBrowser.selectedCount' | transloco: { count: selection().length } }}
            <span class="mono">{{ selection().join(', ') }}</span>
            <button type="button" class="link clear" (click)="selection.set([])">
              {{ 'snapshotBrowser.clear' | transloco }}
            </button>
          }
        </p>
        <label class="field">
          {{ 'snapshotBrowser.form.targetDirectory' | transloco }}
          <input
            name="target"
            [(ngModel)]="targetDirectory"
            [placeholder]="'snapshotBrowser.form.targetDirectoryPlaceholder' | transloco"
          />
          <span class="hint">{{ 'snapshotBrowser.form.targetDirectoryHint' | transloco }}</span>
        </label>
        @if (canRestoreDatabase()) {
          <label class="check">
            <input type="checkbox" name="toDb" [(ngModel)]="toDatabase" />
            <span [innerHTML]="'snapshotBrowser.form.alsoLoadDump' | transloco"></span>
          </label>
          @if (toDatabase) {
            <div class="form-row">
              <label class="field">
                {{ 'snapshotBrowser.form.targetConnection' | transloco }}
                <select name="targetConnection" [(ngModel)]="targetConnectionId">
                  <option value="" disabled>
                    {{ 'snapshotBrowser.form.selectConnection' | transloco }}
                  </option>
                  @for (c of connections(); track c.id) {
                    <option [value]="c.id">{{ c.name }}</option>
                  }
                </select>
              </label>
              <label class="field">
                {{ 'snapshotBrowser.form.newDatabaseName' | transloco }}
                <input
                  name="newDb"
                  [(ngModel)]="newDatabase"
                  [placeholder]="s.database + '_restore'"
                />
                <span class="hint">{{
                  'snapshotBrowser.form.newDatabaseHint'
                    | transloco: { connection: connectionName(targetConnectionId) }
                }}</span>
              </label>
              <label class="field">
                {{ 'snapshotBrowser.form.confirmNameLabel' | transloco }}
                <input name="newDbConfirm" [(ngModel)]="newDatabaseConfirm" autocomplete="off" />
              </label>
            </div>
          }
        }
        <div class="toolbar">
          <button
            type="button"
            class="btn primary"
            (click)="restore(s)"
            [disabled]="submitting() || !databaseConfirmed()"
          >
            {{ 'snapshotBrowser.restore' | transloco }}
          </button>
        </div>
      </section>
    }
  `,
  styles: `
    .crumbs {
      display: flex;
      flex-wrap: wrap;
      gap: 0.3rem;
      align-items: center;
      margin-bottom: 0.75rem;
      font-family: var(--mono);
    }
    .toolbar select {
      width: auto;
    }
    .check-col {
      width: 2rem;
    }
    .clear {
      margin-left: 0.5rem;
    }
    .restore-form {
      margin-top: 1rem;
    }
  `,
})
export class SnapshotBrowser {
  protected readonly auth = inject(AuthService);
  readonly agentId = input.required<string>();
  /** Emitted when a restore job is queued. */
  readonly restored = output<Job>();

  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly transloco = inject(TranslocoService);

  protected readonly globals = GLOBALS;
  protected readonly snapshots = signal<Snapshot[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly policyFilter = signal('');
  protected readonly typeFilter = signal('');

  protected readonly selected = signal<Snapshot | null>(null);
  protected readonly path = signal('/');
  protected readonly nodes = signal<SnapshotNode[]>([]);
  protected readonly treeLoading = signal(false);
  protected readonly treeError = signal<string | null>(null);
  protected readonly selection = signal<string[]>([]);
  protected readonly submitting = signal(false);

  protected targetDirectory = '';
  protected toDatabase = false;
  protected newDatabase = '';
  protected newDatabaseConfirm = '';
  protected targetConnectionId = '';

  private readonly connectionsResource = httpResource<PgConnection[]>(
    () => `/api/admin/agents/${this.agentId()}/connections`,
  );
  protected readonly connections = computed(() => this.connectionsResource.value() ?? []);

  protected readonly policyOptions = computed(() => {
    const seen = new Map<string, string>();
    for (const s of this.snapshots())
      if (s.policyId && !seen.has(s.policyId)) seen.set(s.policyId, s.policyName ?? s.policyId);
    return [...seen].map(([id, name]) => ({ id, name }));
  });

  protected readonly filtered = computed(() =>
    this.snapshots().filter(
      (s) =>
        (!this.policyFilter() || s.policyId === this.policyFilter()) &&
        (!this.typeFilter() || s.type === this.typeFilter()),
    ),
  );

  protected readonly pathCrumbs = computed(() => crumbs(this.path()));

  protected readonly allSelected = computed(
    () => this.nodes().length > 0 && this.nodes().every((n) => this.selection().includes(n.path)),
  );

  protected readonly canRestoreDatabase = computed(() => {
    const s = this.selected();
    return s?.type === 'pg' && !!s.database && s.database !== GLOBALS;
  });

  constructor() {
    queueMicrotask(() => this.load(false));
  }

  load(refresh: boolean): void {
    this.loading.set(true);
    this.loadError.set(null);
    const silent = new HttpContext().set(SILENT_ERRORS, true);
    this.api.snapshots(this.agentId(), refresh, silent).subscribe({
      next: (list) => {
        this.snapshots.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loadError.set(
          this.transloco.translate('snapshotBrowser.cannotReadRepository', {
            error: problemMessage(err),
          }),
        );
        this.loading.set(false);
      },
    });
  }

  protected open(s: Snapshot): void {
    this.selected.set(s);
    this.selection.set([]);
    this.targetDirectory = '';
    this.toDatabase = false;
    this.newDatabase = '';
    this.newDatabaseConfirm = '';
    this.targetConnectionId = s.connectionId ?? '';
    // Start at the backed-up folder: the levels above it hold nothing else.
    this.browse(s.type === 'dir' && s.paths.length === 1 ? toResticTreePath(s.paths[0]) : '/');
  }

  protected connectionName(id: string): string {
    return (
      this.connections().find((c) => c.id === id)?.name ??
      this.transloco.translate('snapshotBrowser.theSelectedConnection')
    );
  }

  protected close(): void {
    this.selected.set(null);
    this.nodes.set([]);
  }

  protected browse(path: string): void {
    const s = this.selected();
    if (!s) return;
    this.path.set(path);
    this.treeLoading.set(true);
    this.treeError.set(null);
    const silent = new HttpContext().set(SILENT_ERRORS, true);
    this.api.snapshotTree(this.agentId(), s.id, path, silent).subscribe({
      next: (nodes) => {
        this.nodes.set(nodes);
        this.treeLoading.set(false);
      },
      error: (err: unknown) => {
        this.treeError.set(
          this.transloco.translate('snapshotBrowser.cannotReadFolder', {
            error: problemMessage(err),
          }),
        );
        this.treeLoading.set(false);
      },
    });
  }

  protected isSelected(path: string): boolean {
    return this.selection().includes(path) || this.coveredByParent(path);
  }

  /** A selected directory already restores everything below it. */
  protected coveredByParent(path: string): boolean {
    return this.selection().some((p) => path.startsWith(p + '/'));
  }

  protected toggle(path: string): void {
    this.selection.update((list) =>
      list.includes(path)
        ? list.filter((p) => p !== path)
        : [...list.filter((p) => !p.startsWith(path + '/')), path],
    );
  }

  protected toggleAll(): void {
    const paths = this.nodes().map((n) => n.path);
    this.selection.update((list) =>
      this.allSelected()
        ? list.filter((p) => !paths.includes(p))
        : [
            ...new Set([
              ...list.filter((p) => !paths.some((n) => p.startsWith(n + '/'))),
              ...paths,
            ]),
          ],
    );
  }

  protected databaseConfirmed(): boolean {
    if (!this.toDatabase) return true;
    if (!this.targetConnectionId) return false;
    const name = this.newDatabase.trim();
    return name.length > 0 && name === this.newDatabaseConfirm.trim();
  }

  protected async restore(s: Snapshot): Promise<void> {
    const newDatabase = this.toDatabase ? this.newDatabase.trim() : null;
    const what =
      this.selection().length === 0
        ? this.transloco.translate('snapshotBrowser.confirmRestore.wholeSnapshot')
        : this.transloco.translate('snapshotBrowser.confirmRestore.items', {
            count: this.selection().length,
          });
    const where = this.targetDirectory.trim() || 'C:\\DupliRestore\\<job id>';
    const message =
      this.transloco.translate('snapshotBrowser.confirmRestore.message', {
        what,
        shortId: s.shortId,
        where,
      }) +
      (newDatabase
        ? this.transloco.translate('snapshotBrowser.confirmRestore.andDatabase', { newDatabase })
        : '');
    if (
      !(await this.confirm.ask(this.transloco.translate('snapshotBrowser.confirmRestore.title'), message, {
        confirmLabel: this.transloco.translate('snapshotBrowser.restore'),
      }))
    )
      return;

    this.submitting.set(true);
    this.api
      .createRestore(this.agentId(), {
        snapshotId: s.id,
        includes: this.selection(),
        targetDirectory: this.targetDirectory.trim() || null,
        newDatabase,
        connectionId: newDatabase ? this.targetConnectionId : null,
      })
      .subscribe({
        next: (job) => {
          this.submitting.set(false);
          this.toasts.success(this.transloco.translate('snapshotBrowser.restoreQueued'));
          this.restored.emit(job);
        },
        error: () => this.submitting.set(false),
      });
  }
}
