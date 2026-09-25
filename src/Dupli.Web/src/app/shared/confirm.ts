import {
  Component,
  ElementRef,
  Injectable,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';

interface ConfirmRequest {
  title: string;
  message: string;
  confirmLabel: string;
  danger: boolean;
  resolve: (ok: boolean) => void;
}

/** In-page replacement for window.confirm (which would block and is not styleable). */
@Injectable({ providedIn: 'root' })
export class ConfirmService {
  private readonly transloco = inject(TranslocoService);
  readonly request = signal<ConfirmRequest | null>(null);

  ask(
    title: string,
    message: string,
    options: { confirmLabel?: string; danger?: boolean } = {},
  ): Promise<boolean> {
    return new Promise((resolve) =>
      this.request.set({
        title,
        message,
        confirmLabel: options.confirmLabel ?? this.transloco.translate('common.confirm'),
        danger: options.danger ?? false,
        resolve,
      }),
    );
  }

  close(ok: boolean): void {
    this.request()?.resolve(ok);
    this.request.set(null);
  }
}

@Component({
  selector: 'app-confirm-dialog',
  imports: [TranslocoModule],
  template: `
    <dialog #dialog class="dialog" (cancel)="confirm.close(false)">
      @if (confirm.request(); as r) {
        <h2>{{ r.title }}</h2>
        <p>{{ r.message }}</p>
        <div class="actions">
          <button type="button" class="btn" (click)="confirm.close(false)">
            {{ 'common.cancel' | transloco }}
          </button>
          <button
            type="button"
            class="btn"
            [class.danger]="r.danger"
            [class.primary]="!r.danger"
            (click)="confirm.close(true)"
          >
            {{ r.confirmLabel }}
          </button>
        </div>
      }
    </dialog>
  `,
})
export class ConfirmDialog {
  protected readonly confirm = inject(ConfirmService);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  constructor() {
    effect(() => {
      const el = this.dialog().nativeElement;
      if (this.confirm.request() && !el.open) el.showModal();
      else if (!this.confirm.request() && el.open) el.close();
    });
  }
}
