import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { UserInfo } from './models';

export const XSRF_COOKIE = 'XSRF-TOKEN';
export const XSRF_FORM_FIELD = '__RequestVerificationToken';

/**
 * BFF session: the browser holds only an HttpOnly cookie. /bff/user tells who is signed in and
 * issues the antiforgery cookie that HttpClient echoes on unsafe requests.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  readonly user = signal<UserInfo | null>(null);

  /** Resolves once the user is known; never resolves when a login redirect is under way. */
  async init(): Promise<void> {
    const user = await firstValueFrom(this.http.get<UserInfo>('/bff/user'));
    this.user.set(user);
    if (!user.authenticated && user.mode !== 'None') {
      this.login();
      await new Promise<never>(() => {});
    }
  }

  login(): void {
    const returnUrl = window.location.pathname + window.location.search;
    window.location.href = '/bff/login?returnUrl=' + encodeURIComponent(returnUrl);
  }

  /** Real form post, so the browser can follow the redirect to the identity provider's sign-out page. */
  logout(): void {
    const form = document.createElement('form');
    form.method = 'POST';
    form.action = '/bff/logout';
    const field = document.createElement('input');
    field.type = 'hidden';
    field.name = XSRF_FORM_FIELD;
    field.value = readCookie(XSRF_COOKIE) ?? '';
    form.appendChild(field);
    document.body.appendChild(form);
    form.submit();
  }
}

export function readCookie(name: string): string | null {
  const prefix = name + '=';
  const match = document.cookie.split('; ').find((c) => c.startsWith(prefix));
  return match ? decodeURIComponent(match.slice(prefix.length)) : null;
}
