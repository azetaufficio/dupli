import { Component, inject } from '@angular/core';
import { TranslocoModule } from '@jsverse/transloco';
import { ToastService } from '../core/toast.service';

@Component({
  selector: 'app-toasts',
  imports: [TranslocoModule],
  template: `
    <div class="toasts" aria-live="polite">
      @for (t of toasts.toasts(); track t.id) {
        <div
          class="toast"
          [class.error]="t.kind === 'error'"
          [class.success]="t.kind === 'success'"
        >
          <span>{{ t.message }}</span>
          <button
            type="button"
            class="link"
            (click)="toasts.dismiss(t.id)"
            [attr.aria-label]="'common.dismiss' | transloco"
          >
            ×
          </button>
        </div>
      }
    </div>
  `,
})
export class Toasts {
  protected readonly toasts = inject(ToastService);
}
