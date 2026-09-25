import { signal } from '@angular/core';
import { Observable } from 'rxjs';
import { Paged } from '../core/models';

/**
 * Keyset-paged list backed by an `ApiService` call: `reload()` fetches the first page, `loadMore()` appends
 * the next one using the server's opaque cursor. Used for jobs/runs/logs, which can no longer be fetched in
 * one shot now that `GET` returns `{ items, next }` instead of a plain array.
 */
export class PagedList<T> {
  readonly items = signal<T[]>([]);
  readonly next = signal<string | null>(null);
  readonly loading = signal(false);
  readonly loadingMore = signal(false);

  constructor(private readonly fetch: (before: string | undefined) => Observable<Paged<T>>) {}

  reload(): void {
    this.loading.set(true);
    this.fetch(undefined).subscribe({
      next: (page) => {
        this.items.set(page.items);
        this.next.set(page.next);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  loadMore(): void {
    const cursor = this.next();
    if (!cursor || this.loadingMore()) return;
    this.loadingMore.set(true);
    this.fetch(cursor).subscribe({
      next: (page) => {
        this.items.update((items) => [...items, ...page.items]);
        this.next.set(page.next);
        this.loadingMore.set(false);
      },
      error: () => this.loadingMore.set(false),
    });
  }
}
