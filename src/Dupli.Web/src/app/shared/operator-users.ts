import { HttpContext } from '@angular/common/http';
import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import { InviteOperatorRequest, OPERATOR_ROLES, OperatorRole, OperatorUser } from '../core/models';
import { ToastService } from '../core/toast.service';
import { Badge } from './badge';
import { ConfirmService } from './confirm';
import { DateTimePipe, RelativeTimePipe } from './format';
import { NotificationPreferences } from './notification-preferences';

/**
 * Invite, re-role, disable and delete operator users. Shared by the Users page (Owner session) and the
 * /admin break-glass page, where `privileged` also allows deleting users who already signed in.
 */
@Component({
  selector: 'app-operator-users',
  imports: [Badge, DateTimePipe, FormsModule, NotificationPreferences, RelativeTimePipe],
  template: `
    <section class="card">
      <h2>Invite</h2>
      <form class="form" (ngSubmit)="invite()" #f="ngForm">
        <div class="form-row">
          <label class="field"
            >Email
            <span class="hint">Entra ID email or UPN. Bound to the account on first sign-in.</span>
            <input name="email" type="email" [(ngModel)]="form.email" required autocomplete="off"
          /></label>
          <label class="field"
            >Role
            <select name="role" [(ngModel)]="form.role">
              @for (r of roles; track r) {
                <option [value]="r">{{ r }}</option>
              }
            </select>
          </label>
        </div>
        @if (error()) {
          <div class="error-box">{{ error() }}</div>
        }
        <div class="toolbar">
          <button type="submit" class="btn primary" [disabled]="f.invalid || saving()">
            Invite
          </button>
          <span class="muted"
            >No email is sent: tell the person to open Dupli and sign in with that account.</span
          >
        </div>
      </form>
    </section>

    <section class="card flush">
      @if (users().length === 0) {
        <div class="empty">No users.</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>User</th>
                <th>Role</th>
                <th>Status</th>
                <th>Last sign-in</th>
                <th>Updated</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (u of users(); track u.id) {
                <tr>
                  <td>
                    <div>{{ u.email }}</div>
                    @if (u.displayName) {
                      <div class="muted">{{ u.displayName }}</div>
                    }
                  </td>
                  <td>
                    <select
                      [ngModel]="u.role"
                      (ngModelChange)="setRole(u, $event)"
                      [attr.aria-label]="'Role of ' + u.email"
                    >
                      @for (r of roles; track r) {
                        <option [value]="r">{{ r }}</option>
                      }
                    </select>
                  </td>
                  <td>
                    <app-badge
                      [value]="u.disabledAt ? 'Disabled' : u.bound ? 'Active' : 'Invited'"
                    />
                  </td>
                  <td [title]="u.lastLoginAt | datetime">
                    {{ u.lastLoginAt ? (u.lastLoginAt | relative) : '—' }}
                  </td>
                  <td [title]="u.updatedAt | datetime" class="muted">
                    {{ u.updatedAt | relative }} · {{ u.updatedBy }}
                  </td>
                  <td class="nowrap">
                    <button type="button" class="btn small" (click)="toggleNotifications(u.id)">
                      Notifications
                    </button>
                    @if (u.disabledAt) {
                      <button type="button" class="btn small" (click)="setDisabled(u, false)">
                        Enable
                      </button>
                    } @else if (u.bound) {
                      <button type="button" class="btn small" (click)="setDisabled(u, true)">
                        Disable
                      </button>
                    }
                    @if (!u.bound || privileged()) {
                      <button type="button" class="btn small danger" (click)="remove(u)">
                        Delete
                      </button>
                    }
                  </td>
                </tr>
                @if (expandedUserId() === u.id) {
                  <tr>
                    <td colspan="6" class="notification-preferences-cell">
                      <app-notification-preferences [userId]="u.id" />
                    </td>
                  </tr>
                }
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
})
export class OperatorUsers implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly privileged = input(false);

  protected readonly roles = OPERATOR_ROLES;
  protected readonly users = signal<OperatorUser[]>([]);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly expandedUserId = signal<string | null>(null);
  protected form: InviteOperatorRequest = { email: '', role: 'Viewer' };

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.api.users().subscribe((users) => this.users.set(users));
  }

  protected toggleNotifications(userId: string): void {
    this.expandedUserId.set(this.expandedUserId() === userId ? null : userId);
  }

  protected invite(): void {
    this.saving.set(true);
    this.error.set(null);
    const request = { ...this.form, email: this.form.email.trim() };
    this.api.inviteUser(request, new HttpContext().set(SILENT_ERRORS, true)).subscribe({
      next: (u) => {
        this.toasts.success(`${u.email} invited as ${u.role}`);
        this.form = { email: '', role: 'Viewer' };
        this.saving.set(false);
        this.reload();
      },
      error: (e) => {
        this.error.set(problemMessage(e));
        this.saving.set(false);
      },
    });
  }

  protected setRole(user: OperatorUser, role: OperatorRole): void {
    this.api.setUserRole(user.id, role).subscribe({
      next: (u) => this.toasts.success(`${u.email} is now ${u.role}`),
      // Refused (e.g. last owner): put the select back to the stored role.
      error: () => this.reload(),
      complete: () => this.reload(),
    });
  }

  protected async setDisabled(user: OperatorUser, disabled: boolean): Promise<void> {
    if (
      disabled &&
      !(await this.confirm.ask(
        'Disable user',
        `${user.email} loses access immediately, including open sessions.`,
        { confirmLabel: 'Disable', danger: true },
      ))
    )
      return;
    this.api.setUserDisabled(user.id, disabled).subscribe(() => this.reload());
  }

  protected async remove(user: OperatorUser): Promise<void> {
    const message = user.bound
      ? `${user.email} has already signed in. Deleting it removes the account binding; disabling keeps it.`
      : `The invitation for ${user.email} will be removed.`;
    if (!(await this.confirm.ask('Delete user', message, { confirmLabel: 'Delete', danger: true })))
      return;
    this.api.deleteUser(user.id).subscribe(() => this.reload());
  }
}
