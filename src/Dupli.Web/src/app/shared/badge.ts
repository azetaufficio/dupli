import { Component, computed, inject, input } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';

type Tone = 'ok' | 'warn' | 'bad' | 'info' | 'muted';

const TONES: Record<string, Tone> = {
  Succeeded: 'ok',
  Active: 'ok',
  Online: 'ok',
  SucceededWithWarnings: 'warn',
  Pending: 'muted',
  Assigned: 'info',
  Running: 'info',
  Failed: 'bad',
  TimedOut: 'bad',
  Interrupted: 'bad',
  Missed: 'warn',
  Offline: 'bad',
  Cancelled: 'muted',
  Disabled: 'muted',
  Error: 'bad',
  Fatal: 'bad',
  Warning: 'warn',
  Information: 'info',
  Open: 'bad',
  Resolved: 'ok',
  RolledBack: 'bad',
  'Up to date': 'ok',
  'Update available': 'warn',
  'Update failed': 'bad',
  'No launcher': 'muted',
  Invited: 'info',
};

/** Colored status pill; the tone and translated label are derived from well-known state names. */
@Component({
  selector: 'app-badge',
  template: `<span class="badge" [class]="'badge tone-' + tone()">{{ label() }}</span>`,
})
export class Badge {
  private readonly transloco = inject(TranslocoService);

  readonly value = input.required<string | null | undefined>();
  readonly text = input<string>();

  protected readonly tone = computed<Tone>(() => TONES[this.value() ?? ''] ?? 'muted');
  protected readonly label = computed(() => {
    this.transloco.activeLang(); // re-run when the language changes
    const raw = this.value() ?? '';
    if (this.text()) return this.text()!;
    return raw in TONES ? this.transloco.translate('status.' + raw) : raw || '—';
  });
}
