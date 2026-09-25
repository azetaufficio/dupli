import { Component, inject, input } from '@angular/core';
import { TranslocoModule } from '@jsverse/transloco';
import { AuthService } from '../core/auth.service';

/** Where the server sends an Entra ID account that is not (or no longer) a Dupli user. No session exists. */
@Component({
  selector: 'app-access-denied',
  imports: [TranslocoModule],
  template: `
    <section class="card notice">
      <h1>{{ 'accessDenied.title' | transloco }}</h1>
      <p>
        @if (email()) {
          <strong>{{ email() }}</strong>
          {{ 'accessDenied.accountNotEnabled' | transloco }}
        } @else {
          {{ 'accessDenied.thisAccountNotEnabled' | transloco }}
        }
        {{ 'accessDenied.askOwner' | transloco }}
      </p>
      <button type="button" class="btn" (click)="auth.loginWithAnotherAccount()">
        {{ 'accessDenied.signInAnotherAccount' | transloco }}
      </button>
    </section>
  `,
})
export class AccessDeniedPage {
  protected readonly auth = inject(AuthService);
  readonly email = input<string>();
}
