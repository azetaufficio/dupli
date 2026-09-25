import { Component, inject, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiService } from '../core/api.service';
import { OperatorNotification } from '../core/models';
import { alertKindLabel } from '../shared/notification-preferences';
import { DateTimePipe, RelativeTimePipe } from '../shared/format';

const PAGE_SIZE = 50;

/** Paginated feed behind the bell: unread filter, mark-as-read on click, then off to the agent or policy. */
@Component({
  selector: 'app-notifications',
  imports: [DateTimePipe, RelativeTimePipe],
  template: `
    <div class="page-header">
      <div>
        <h1>Notifications</h1>
        <p class="muted">Alert opens and resolutions you are subscribed to.</p>
      </div>
      <div class="toolbar">
        <button
          type="button"
          class="btn"
          [class.primary]="unreadOnly()"
          (click)="setUnreadOnly(true)"
        >
          Unread
        </button>
        <button
          type="button"
          class="btn"
          [class.primary]="!unreadOnly()"
          (click)="setUnreadOnly(false)"
        >
          All
        </button>
        @if (items().some((n) => !n.readAt)) {
          <button type="button" class="btn small" (click)="markAllRead()">Mark all read</button>
        }
      </div>
    </div>
    <section class="card flush">
      @if (items().length === 0 && !loading()) {
        <div class="empty">No notifications.</div>
      } @else {
        <ul class="notification-list">
          @for (n of items(); track n.id) {
            <li [class.unread]="!n.readAt" (click)="open(n)">
              <div class="notification-row">
                <span class="nowrap">{{ label(n) }}</span>
                <span>{{ n.subject }}</span>
                <span class="muted nowrap" [title]="n.createdAt | datetime">{{
                  n.createdAt | relative
                }}</span>
              </div>
            </li>
          }
        </ul>
      }
      @if (hasMore()) {
        <div class="toolbar" style="padding: 0.75rem">
          <button type="button" class="btn" [disabled]="loading()" (click)="loadMore()">
            Load more
          </button>
        </div>
      }
    </section>
  `,
  styles: [
    `
      .notification-list {
        list-style: none;
        margin: 0;
        padding: 0;
      }
      .notification-list li {
        padding: 0.6rem 1rem;
        border-bottom: 1px solid var(--border);
        cursor: pointer;
      }
      .notification-list li:hover {
        background: var(--hover);
      }
      .notification-list li.unread {
        font-weight: 600;
      }
      .notification-row {
        display: flex;
        gap: 0.75rem;
        align-items: baseline;
      }
      .notification-row > span:nth-child(2) {
        flex: 1;
        font-weight: normal;
      }
    `,
  ],
})
export class NotificationsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  protected readonly items = signal<OperatorNotification[]>([]);
  protected readonly unreadOnly = signal(false);
  protected readonly loading = signal(false);
  protected readonly hasMore = signal(false);

  protected readonly label = (n: OperatorNotification) =>
    `${alertKindLabel(n.kind)} · ${n.event === 'Resolved' ? 'resolved' : 'opened'}`;

  ngOnInit(): void {
    this.load(true);
  }

  protected setUnreadOnly(value: boolean): void {
    this.unreadOnly.set(value);
    this.load(true);
  }

  protected loadMore(): void {
    this.load(false);
  }

  private load(reset: boolean): void {
    const before = reset ? undefined : this.items().at(-1)?.createdAt;
    this.loading.set(true);
    this.api
      .myNotifications({ unreadOnly: this.unreadOnly(), before, limit: PAGE_SIZE })
      .subscribe((page) => {
        this.items.set(reset ? page : [...this.items(), ...page]);
        this.hasMore.set(page.length === PAGE_SIZE);
        this.loading.set(false);
      });
  }

  protected open(n: OperatorNotification): void {
    if (!n.readAt) {
      const readAt = new Date().toISOString();
      this.items.set(this.items().map((i) => (i.id === n.id ? { ...i, readAt } : i)));
      this.api.markNotificationRead(n.id).subscribe();
    }
    if (n.agentId) this.router.navigate(['/agents', n.agentId]);
    else if (n.policyId) this.router.navigate(['/policies', n.policyId]);
  }

  protected markAllRead(): void {
    this.api.markAllNotificationsRead().subscribe(() => {
      const now = new Date().toISOString();
      this.items.set(this.items().map((n) => ({ ...n, readAt: n.readAt ?? now })));
    });
  }
}
