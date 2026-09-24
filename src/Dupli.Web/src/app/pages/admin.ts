import { HttpClient, HttpContext, HttpErrorResponse } from '@angular/common/http';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { problemMessage, SILENT_ERRORS } from '../core/http-errors.interceptor';
import { OperatorUsers } from '../shared/operator-users';

type State = 'checking' | 'disabled' | 'locked' | 'open';

/**
 * Break-glass: the admin key, sent once, opens a 15-minute server session limited to user management, for
 * when no owner can sign in any more. The key is never stored in the browser. Disabled (404) without a key.
 */
@Component({
  selector: 'app-admin',
  imports: [FormsModule, OperatorUsers],
  template: `
    <div class="page-header">
      <div>
        <h1>Break-glass user management</h1>
        <p class="muted">
          Admin key access for when no owner can sign in. The session lasts 15 minutes and only
          manages users.
        </p>
      </div>
      @if (state() === 'open') {
        <button type="button" class="btn" (click)="close()">Close session</button>
      }
    </div>

    @switch (state()) {
      @case ('disabled') {
        <section class="card notice">
          <h2>Disabled</h2>
          <p>
            Set <code>Dupli__Admin__ApiKey</code> on the server and restart it to use this page.
            Remove it again afterwards.
          </p>
        </section>
      }
      @case ('locked') {
        <section class="card">
          <form class="form" (ngSubmit)="open()" #f="ngForm">
            <label class="field"
              >Admin key
              <input name="key" type="password" [(ngModel)]="key" required autocomplete="off"
            /></label>
            @if (error()) {
              <div class="error-box">{{ error() }}</div>
            }
            <div class="toolbar">
              <button type="submit" class="btn primary" [disabled]="f.invalid || busy()">
                Open session
              </button>
            </div>
          </form>
        </section>
      }
      @case ('open') {
        <app-operator-users [privileged]="true" />
      }
    }
  `,
})
export class AdminPage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly silent = { context: new HttpContext().set(SILENT_ERRORS, true) };

  protected readonly state = signal<State>('checking');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected key = '';

  async ngOnInit(): Promise<void> {
    // Also issues the antiforgery token bound to the break-glass session.
    if (await this.sessionOpen()) this.state.set('open');
    else this.state.set((await this.enabled()) ? 'locked' : 'disabled');
  }

  protected async open(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.http.post('/bff/admin-login', { key: this.key }, this.silent));
      this.key = '';
      this.state.set((await this.sessionOpen()) ? 'open' : 'locked');
    } catch (e) {
      this.error.set(problemMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected async close(): Promise<void> {
    await firstValueFrom(this.http.post('/bff/admin-logout', null, this.silent));
    this.state.set('locked');
  }

  private async sessionOpen(): Promise<boolean> {
    try {
      await firstValueFrom(this.http.get('/bff/admin-session', this.silent));
      return true;
    } catch {
      return false;
    }
  }

  private async enabled(): Promise<boolean> {
    try {
      await firstValueFrom(this.http.get('/bff/admin-login', this.silent));
      return true;
    } catch (e) {
      return !(e instanceof HttpErrorResponse && e.status === 404);
    }
  }
}
