import { HttpContext } from '@angular/common/http';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import { Job, Snapshot, SnapshotNode } from '../core/models';
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
 * Snapshot list and browser of an agent's repository (read by the server), plus the restore form.
 * The restore itself is a job run by the agent, into an alternative directory.
 */
@Component({
  selector: 'app-snapshot-browser',
  imports: [FormsModule, BytesPipe, DateTimePipe],
  template: `
    @if (!selected()) {
      <div class="toolbar">
        <select
          [ngModel]="policyFilter()"
          (ngModelChange)="policyFilter.set($event)"
          aria-label="Policy"
        >
          <option value="">All policies</option>
          @for (p of policyOptions(); track p.id) {
            <option [value]="p.id">{{ p.name }}</option>
          }
        </select>
        <select [ngModel]="typeFilter()" (ngModelChange)="typeFilter.set($event)" aria-label="Type">
          <option value="">All types</option>
          <option value="dir">Folders</option>
          <option value="pg">PostgreSQL</option>
        </select>
        <button type="button" class="btn" (click)="load(true)" [disabled]="loading()">
          Refresh
        </button>
      </div>
      @if (loading() && snapshots().length === 0) {
        <p class="muted">Reading the repository…</p>
      } @else if (loadError()) {
        <p class="error-box">{{ loadError() }}</p>
      } @else if (filtered().length === 0) {
        <div class="empty">No snapshots.</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Time</th>
                <th>Policy</th>
                <th>Source</th>
                <th>Content</th>
                <th>Snapshot</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (s of filtered(); track s.id) {
                <tr>
                  <td class="nowrap">{{ s.time | datetime }}</td>
                  <td>{{ s.policyName ?? (s.policyId ? 'deleted policy' : '—') }}</td>
                  <td>{{ s.sourceId ?? '—' }}</td>
                  <td class="mono">
                    @if (s.type === 'pg') {
                      {{
                        s.database === globals ? 'roles and tablespaces' : 'database ' + s.database
                      }}
                    } @else {
                      {{ s.paths.join(', ') }}
                    }
                  </td>
                  <td class="mono">{{ s.shortId }}</td>
                  <td class="num">
                    <button type="button" class="btn small" (click)="open(s)">
                      {{ s.type === 'pg' ? 'Restore' : 'Browse' }}
                    </button>
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
        <button type="button" class="btn" (click)="close()">← Snapshots</button>
        <span class="muted"
          >Snapshot <span class="mono">{{ s.shortId }}</span> — {{ s.time | datetime }}
          @if (s.policyName) {
            — {{ s.policyName }} / {{ s.sourceId }}
          }
        </span>
      </div>

      <nav class="crumbs" aria-label="Path">
        <button type="button" class="link" (click)="browse('/')">/</button>
        @for (c of pathCrumbs(); track c.path) {
          @if (!$first) {
            <span class="muted">/</span>
          }
          <button type="button" class="link" (click)="browse(c.path)">{{ c.name }}</button>
        }
      </nav>

      @if (treeLoading()) {
        <p class="muted">Loading…</p>
      } @else if (nodes().length === 0) {
        <div class="empty">Empty directory.</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th class="check-col">
                  <input
                    type="checkbox"
                    aria-label="Select all"
                    [checked]="allSelected()"
                    (change)="toggleAll()"
                  />
                </th>
                <th>Name</th>
                <th class="num">Size</th>
                <th>Modified</th>
              </tr>
            </thead>
            <tbody>
              @for (n of nodes(); track n.path) {
                <tr>
                  <td class="check-col">
                    <input
                      type="checkbox"
                      [attr.aria-label]="'Select ' + n.name"
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
        <h2>Restore</h2>
        <p class="muted">
          @if (selection().length === 0) {
            Whole snapshot.
          } @else {
            {{ selection().length }} selected:
            <span class="mono">{{ selection().join(', ') }}</span>
            <button type="button" class="link clear" (click)="selection.set([])">clear</button>
          }
        </p>
        <label class="field">
          Target directory on the VM
          <input
            name="target"
            [(ngModel)]="targetDirectory"
            placeholder="Default: C:\\DupliRestore\\<job id>"
          />
          <span class="hint"
            >Must be new or empty and outside every backed-up path. Production data is never
            overwritten.</span
          >
        </label>
        @if (canRestoreDatabase()) {
          <label class="check">
            <input type="checkbox" name="toDb" [(ngModel)]="toDatabase" />
            Also load the dump into a new database (<code>pg_restore</code>)
          </label>
          @if (toDatabase) {
            <div class="form-row">
              <label class="field">
                New database name
                <input
                  name="newDb"
                  [(ngModel)]="newDatabase"
                  [placeholder]="s.database + '_restore'"
                />
                <span class="hint"
                  >Created on {{ s.sourceId }}; the restore fails if it already exists.</span
                >
              </label>
              <label class="field">
                Type the name again to confirm
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
            Restore
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
  readonly agentId = input.required<string>();
  /** Emitted when a restore job is queued. */
  readonly restored = output<Job>();

  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

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
  protected readonly selection = signal<string[]>([]);
  protected readonly submitting = signal(false);

  protected targetDirectory = '';
  protected toDatabase = false;
  protected newDatabase = '';
  protected newDatabaseConfirm = '';

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
    return s?.type === 'pg' && !!s.database && s.database !== GLOBALS && !!s.policyName;
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
        this.loadError.set(`Cannot read the repository: ${problemMessage(err)}`);
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
    // Start at the backed-up folder: the levels above it hold nothing else.
    this.browse(s.type === 'dir' && s.paths.length === 1 ? s.paths[0] : '/');
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
    this.api.snapshotTree(this.agentId(), s.id, path).subscribe({
      next: (nodes) => {
        this.nodes.set(nodes);
        this.treeLoading.set(false);
      },
      error: () => this.treeLoading.set(false),
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
    const name = this.newDatabase.trim();
    return name.length > 0 && name === this.newDatabaseConfirm.trim();
  }

  protected async restore(s: Snapshot): Promise<void> {
    const newDatabase = this.toDatabase ? this.newDatabase.trim() : null;
    const what =
      this.selection().length === 0 ? 'the whole snapshot' : `${this.selection().length} item(s)`;
    const where = this.targetDirectory.trim() || 'C:\\DupliRestore\\<job id>';
    const message =
      `Restores ${what} of ${s.shortId} to ${where} on the VM.` +
      (newDatabase ? ` Then creates database "${newDatabase}" and loads the dump into it.` : '');
    if (!(await this.confirm.ask('Restore?', message, { confirmLabel: 'Restore' }))) return;

    this.submitting.set(true);
    this.api
      .createRestore(this.agentId(), {
        snapshotId: s.id,
        includes: this.selection(),
        targetDirectory: this.targetDirectory.trim() || null,
        newDatabase,
      })
      .subscribe({
        next: (job) => {
          this.submitting.set(false);
          this.toasts.success('Restore queued: the agent picks it up at its next poll');
          this.restored.emit(job);
        },
        error: () => this.submitting.set(false),
      });
  }
}
