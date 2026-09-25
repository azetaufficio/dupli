import { Component, inject, input, OnInit, signal } from '@angular/core';
import { ALERT_KINDS, AlertKind, NotificationPreference } from '../core/models';
import { ApiService } from '../core/api.service';
import { problemMessage } from '../core/http-errors.interceptor';
import { ToastService } from '../core/toast.service';

/** "AgentOffline" -> "Agent offline". Also used by the /notifications feed. */
export function alertKindLabel(kind: AlertKind): string {
  const spaced = kind.replace(/([a-z])([A-Z])/g, '$1 $2');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

const DESCRIPTIONS: Record<AlertKind, string> = {
  AgentOffline: 'An agent has not sent a heartbeat for a while.',
  BackupFailed: 'A backup job failed or timed out.',
  BackupMissed: 'A scheduled backup did not run.',
  BackupTooOld: 'A policy has had no successful backup for too long.',
  RepositoryCheckFailed: 'A repository consistency check failed.',
  RestoreTestFailed: 'A scheduled restore test failed.',
  AgentUpdateFailed: 'An agent update was rolled back.',
  AgentOutdated: 'An agent has been stuck off the desired version for a while.',
};

/**
 * Per-kind e-mail / in-app toggles: the current user's own preferences at `/settings/notifications`
 * (`userId` unset), or an Owner editing another user's from the Users page (`userId` set).
 */
@Component({
  selector: 'app-notification-preferences',
  template: `
    @if (loaded()) {
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Alert</th>
              <th>Description</th>
              <th>Mail</th>
              <th>In-app</th>
            </tr>
          </thead>
          <tbody>
            @for (p of preferences(); track p.kind) {
              <tr>
                <td class="nowrap">{{ label(p.kind) }}</td>
                <td class="muted">{{ description(p.kind) }}</td>
                <td>
                  <input
                    type="checkbox"
                    [attr.aria-label]="label(p.kind) + ' by mail'"
                    [checked]="p.email"
                    [disabled]="saving() === p.kind"
                    (change)="toggle(p, 'email', $any($event.target).checked)"
                  />
                </td>
                <td>
                  <input
                    type="checkbox"
                    [attr.aria-label]="label(p.kind) + ' in-app'"
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

  protected readonly label = alertKindLabel;
  protected description(kind: AlertKind): string {
    return DESCRIPTIONS[kind];
  }

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
