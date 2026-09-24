import { Component, inject, input } from '@angular/core';
import { AuthService } from '../core/auth.service';

/** Where the server sends an Entra ID account that is not (or no longer) a Dupli user. No session exists. */
@Component({
  selector: 'app-access-denied',
  template: `
    <section class="card notice">
      <h1>Access denied</h1>
      <p>
        @if (email()) {
          <strong>{{ email() }}</strong> is not enabled in Dupli.
        } @else {
          This account is not enabled in Dupli.
        }
        Ask an owner to invite you, then sign in again.
      </p>
      <button type="button" class="btn" (click)="auth.loginWithAnotherAccount()">
        Sign in with another account
      </button>
    </section>
  `,
})
export class AccessDeniedPage {
  protected readonly auth = inject(AuthService);
  readonly email = input<string>();
}
