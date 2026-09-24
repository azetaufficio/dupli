import { Component, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { AuthService, isStandalonePath } from './core/auth.service';
import { ConfirmDialog } from './shared/confirm';
import { Toasts } from './shared/toasts';

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ConfirmDialog, Toasts],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected readonly auth = inject(AuthService);

  constructor() {
    // Leaving /admin or /access-denied for a normal page needs an operator session again.
    inject(Router)
      .events.pipe(
        filter((e): e is NavigationEnd => e instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe((e) => {
        const standalone = isStandalonePath(e.urlAfterRedirects.split(/[?#]/)[0]);
        this.auth.standalone.set(standalone);
        const user = this.auth.user();
        if (!standalone && user && !user.authenticated && user.mode !== 'None') this.auth.login();
      });
  }
}
