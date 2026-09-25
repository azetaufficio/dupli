import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, interval, startWith, switchMap } from 'rxjs';
import { ApiService } from './core/api.service';
import { AuthService, isStandalonePath } from './core/auth.service';
import { RelativeTimePipe } from './shared/format';
import { OperatorNotification } from './core/models';
import { ConfirmDialog } from './shared/confirm';
import { unreadBadgeLabel } from './shared/notification-badge';
import { Toasts } from './shared/toasts';

const UNREAD_POLL_INTERVAL = 60_000;

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ConfirmDialog, Toasts, RelativeTimePipe],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  protected readonly menuOpen = signal(false);
  protected readonly bellOpen = signal(false);
  protected readonly unreadCount = signal(0);
  protected readonly preview = signal<OperatorNotification[]>([]);
  protected readonly bellBadge = computed(() => unreadBadgeLabel(this.unreadCount()));

  constructor() {
    // Leaving /admin or /access-denied for a normal page needs an operator session again.
    this.router.events
      .pipe(
        filter((e): e is NavigationEnd => e instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe((e) => {
        this.menuOpen.set(false);
        this.bellOpen.set(false);
        const standalone = isStandalonePath(e.urlAfterRedirects.split(/[?#]/)[0]);
        this.auth.standalone.set(standalone);
        const user = this.auth.user();
        if (!standalone && user && !user.authenticated && user.mode !== 'None') this.auth.login();
      });

    // Polls even while the dropdown is closed: the badge should update on its own.
    interval(UNREAD_POLL_INTERVAL)
      .pipe(
        startWith(0),
        filter(() => !!this.auth.role() && !this.auth.standalone()),
        switchMap(() => this.api.myUnreadNotificationCount()),
        takeUntilDestroyed(),
      )
      .subscribe((r) => this.unreadCount.set(r.count));
  }

  protected toggleBell(): void {
    const next = !this.bellOpen();
    this.bellOpen.set(next);
    if (next) this.api.myNotifications({ limit: 10 }).subscribe((items) => this.preview.set(items));
  }

  protected markAllRead(): void {
    this.api.markAllNotificationsRead().subscribe(() => {
      this.unreadCount.set(0);
      const now = new Date().toISOString();
      this.preview.set(this.preview().map((n) => ({ ...n, readAt: n.readAt ?? now })));
    });
  }

  protected openNotification(n: OperatorNotification): void {
    this.bellOpen.set(false);
    if (!n.readAt) {
      this.api.markNotificationRead(n.id).subscribe();
      this.unreadCount.update((c) => Math.max(0, c - 1));
    }
    if (n.agentId) this.router.navigate(['/agents', n.agentId]);
    else if (n.policyId) this.router.navigate(['/policies', n.policyId]);
    else this.router.navigate(['/notifications']);
  }
}
