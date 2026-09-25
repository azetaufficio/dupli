import { HttpContext } from '@angular/common/http';
import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';
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
  imports: [Badge, DateTimePipe, FormsModule, NotificationPreferences, RelativeTimePipe, TranslocoModule],
  template: `
    <section class="card">
      <h2>{{ 'operatorUsers.invite.title' | transloco }}</h2>
      <form class="form" (ngSubmit)="invite()" #f="ngForm">
        <div class="form-row">
          <label class="field"
            >{{ 'operatorUsers.invite.emailLabel' | transloco }}
            <span class="hint">{{ 'operatorUsers.invite.emailHint' | transloco }}</span>
            <input name="email" type="email" [(ngModel)]="form.email" required autocomplete="off"
          /></label>
          <label class="field"
            >{{ 'operatorUsers.invite.roleLabel' | transloco }}
            <select name="role" [(ngModel)]="form.role">
              @for (r of roles; track r) {
                <option [value]="r">{{ 'operatorUsers.roles.' + r | transloco }}</option>
              }
            </select>
          </label>
        </div>
        @if (error()) {
          <div class="error-box">{{ error() }}</div>
        }
        <div class="toolbar">
          <button type="submit" class="btn primary" [disabled]="f.invalid || saving()">
            {{ 'operatorUsers.invite.submit' | transloco }}
          </button>
          <span class="muted">{{ 'operatorUsers.invite.noEmailSent' | transloco }}</span>
        </div>
      </form>
    </section>

    <section class="card flush">
      @if (users().length === 0) {
        <div class="empty">{{ 'operatorUsers.empty' | transloco }}</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{{ 'operatorUsers.table.user' | transloco }}</th>
                <th>{{ 'operatorUsers.table.role' | transloco }}</th>
                <th>{{ 'operatorUsers.table.status' | transloco }}</th>
                <th>{{ 'operatorUsers.table.lastSignIn' | transloco }}</th>
                <th>{{ 'operatorUsers.table.updated' | transloco }}</th>
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
                      [attr.aria-label]="
                        'operatorUsers.roleOfAriaLabel' | transloco: { email: u.email }
                      "
                    >
                      @for (r of roles; track r) {
                        <option [value]="r">{{ 'operatorUsers.roles.' + r | transloco }}</option>
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
                      {{ 'operatorUsers.notifications' | transloco }}
                    </button>
                    @if (u.disabledAt) {
                      <button type="button" class="btn small" (click)="setDisabled(u, false)">
                        {{ 'operatorUsers.enable' | transloco }}
                      </button>
                    } @else if (u.bound) {
                      <button type="button" class="btn small" (click)="setDisabled(u, true)">
                        {{ 'operatorUsers.disable' | transloco }}
                      </button>
                    }
                    @if (!u.bound || privileged()) {
                      <button type="button" class="btn small danger" (click)="remove(u)">
                        {{ 'common.delete' | transloco }}
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
  private readonly transloco = inject(TranslocoService);

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
        this.toasts.success(
          this.transloco.translate('operatorUsers.toasts.invited', {
            email: u.email,
            role: this.transloco.translate('operatorUsers.roles.' + u.role),
          }),
        );
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
      next: (u) =>
        this.toasts.success(
          this.transloco.translate('operatorUsers.toasts.roleChanged', {
            email: u.email,
            role: this.transloco.translate('operatorUsers.roles.' + u.role),
          }),
        ),
      // Refused (e.g. last owner): put the select back to the stored role.
      error: () => this.reload(),
      complete: () => this.reload(),
    });
  }

  protected async setDisabled(user: OperatorUser, disabled: boolean): Promise<void> {
    if (
      disabled &&
      !(await this.confirm.ask(
        this.transloco.translate('operatorUsers.confirmDisable.title'),
        this.transloco.translate('operatorUsers.confirmDisable.message', { email: user.email }),
        { confirmLabel: this.transloco.translate('operatorUsers.disable'), danger: true },
      ))
    )
      return;
    this.api.setUserDisabled(user.id, disabled).subscribe(() => this.reload());
  }

  protected async remove(user: OperatorUser): Promise<void> {
    const message = user.bound
      ? this.transloco.translate('operatorUsers.confirmDelete.boundMessage', { email: user.email })
      : this.transloco.translate('operatorUsers.confirmDelete.invitationMessage', {
          email: user.email,
        });
    if (
      !(await this.confirm.ask(
        this.transloco.translate('operatorUsers.confirmDelete.title'),
        message,
        { confirmLabel: this.transloco.translate('common.delete'), danger: true },
      ))
    )
      return;
    this.api.deleteUser(user.id).subscribe(() => this.reload());
  }
}
