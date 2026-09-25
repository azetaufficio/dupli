import { Component, inject, input, OnInit, signal } from '@angular/core';
import { TranslocoModule } from '@jsverse/transloco';
import { ALERT_KINDS, AlertKind, NotificationPreference } from '../core/models';
import { ApiService } from '../core/api.service';
import { problemMessage } from '../core/http-errors.interceptor';
import { ToastService } from '../core/toast.service';

/**
 * Per-kind e-mail / in-app toggles: the current user's own preferences at `/settings/notifications`
 * (`userId` unset), or an Owner editing another user's from the Users page (`userId` set).
 */
@Component({
  selector: 'app-notification-preferences',
  imports: [TranslocoModule],
  template: `
    @if (loaded()) {
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>{{ 'notificationPreferences.table.alert' | transloco }}</th>
              <th>{{ 'notificationPreferences.table.description' | transloco }}</th>
              <th>{{ 'notificationPreferences.table.mail' | transloco }}</th>
              <th>{{ 'notificationPreferences.table.inApp' | transloco }}</th>
            </tr>
          </thead>
          <tbody>
            @for (p of preferences(); track p.kind) {
              <tr>
                <td class="nowrap">{{ 'alertKind.' + p.kind + '.label' | transloco }}</td>
                <td class="muted">{{ 'alertKind.' + p.kind + '.description' | transloco }}</td>
                <td>
                  <input
                    type="checkbox"
                    [attr.aria-label]="
                      'notificationPreferences.mailAriaLabel'
                        | transloco: { alert: 'alertKind.' + p.kind + '.label' | transloco }
                    "
                    [checked]="p.email"
                    [disabled]="saving() === p.kind"
                    (change)="toggle(p, 'email', $any($event.target).checked)"
                  />
                </td>
                <td>
                  <input
                    type="checkbox"
                    [attr.aria-label]="
                      'notificationPreferences.inAppAriaLabel'
                        | transloco: { alert: 'alertKind.' + p.kind + '.label' | transloco }
                    "
                    [checked]="p.inApp"
                    [disabled]="saving() === p.kind"
                    (change)="toggle(p, 'inApp', $any($event.target).checked)"
                  />
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class NotificationPreferences implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);

  /** Whose preferences to show/edit. Unset: the signed-in user, via /api/me. */
  readonly userId = input<string | null>(null);

  protected readonly preferences = signal<NotificationPreference[]>([]);
  protected readonly loaded = signal(false);
  protected readonly saving = signal<AlertKind | null>(null);

  ngOnInit(): void {
    this.reload();
  }

  private reload(): void {
    const id = this.userId();
    const request = id
      ? this.api.userNotificationPreferences(id)
      : this.api.myNotificationPreferences();
    request.subscribe((preferences) => {
      // Stable order regardless of what the server returns.
      const byKind = new Map(preferences.map((p) => [p.kind, p]));
      this.preferences.set(
        ALERT_KINDS.map((kind) => byKind.get(kind) ?? { kind, email: false, inApp: true }),
      );
      this.loaded.set(true);
    });
  }

  protected toggle(
    preference: NotificationPreference,
    field: 'email' | 'inApp',
    value: boolean,
  ): void {
    const updated: NotificationPreference = { ...preference, [field]: value };
    this.preferences.update((list) => list.map((p) => (p.kind === preference.kind ? updated : p)));
    this.saving.set(preference.kind);
    const id = this.userId();
    const request = id
      ? this.api.setUserNotificationPreferences(id, [updated])
      : this.api.setMyNotificationPreferences([updated]);
    request.subscribe({
      next: () => this.saving.set(null),
      error: (e) => {
        this.toasts.error(problemMessage(e));
        this.saving.set(null);
        this.reload();
      },
    });
  }
}
