import { HttpClient } from '@angular/common/http';
import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { ApiService } from './api.service';
import { resolveLanguage } from './language';
import { Language, OPERATOR_ROLES, OperatorRole, UserInfo } from './models';

export const XSRF_COOKIE = 'XSRF-TOKEN';
export const XSRF_FORM_FIELD = '__RequestVerificationToken';

/** Pages that work without an operator session: break-glass (/admin) and the sign-in refusal. */
export const STANDALONE_PATHS = ['/admin', '/access-denied'];

export function isStandalonePath(path: string): boolean {
  return STANDALONE_PATHS.some((p) => path === p || path.startsWith(p + '/'));
}

/** True when `role` includes the permissions of `minimum`. */
export function hasRole(role: OperatorRole | null | undefined, minimum: OperatorRole): boolean {
  return !!role && OPERATOR_ROLES.indexOf(role) >= OPERATOR_ROLES.indexOf(minimum);
}

/**
 * BFF session: the browser holds only an HttpOnly cookie. /bff/user tells who is signed in (and with which
 * role) and issues the antiforgery cookie that HttpClient echoes on unsafe requests. The server enforces the
 * role; the UI only hides what the user cannot do.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly api = inject(ApiService);
  private readonly transloco = inject(TranslocoService);
  readonly user = signal<UserInfo | null>(null);
  readonly standalone = signal(false);

  readonly role = computed(() => this.user()?.role ?? null);
  /** Runs backups: policies, connections, agents, jobs, snapshot contents, restores. */
  readonly canOperate = computed(() => hasRole(this.role(), 'Operator'));
  /** Users, storage targets, releases. */
  readonly isOwner = computed(() => hasRole(this.role(), 'Owner'));

  constructor() {
    // Keeps the <html lang> attribute (accessibility, form controls) in sync with the active language.
    effect(() => document.documentElement.setAttribute('lang', this.transloco.activeLang()));
  }

  /** Resolves once the user is known; never resolves when a login redirect is under way. */
  async init(): Promise<void> {
    this.standalone.set(isStandalonePath(window.location.pathname));
    const user = await firstValueFrom(this.http.get<UserInfo>('/bff/user'));
    this.user.set(user);
    this.transloco.setActiveLang(resolveLanguage(user.language));
    if (!user.authenticated && user.mode !== 'None' && !this.standalone()) {
      this.login();
      await new Promise<never>(() => {});
    }
  }

  /** Applies at once (no rebuild needed) and persists for next time, on any device. */
  setLanguage(language: Language): void {
    this.transloco.setActiveLang(language);
    this.user.update((u) => (u ? { ...u, language } : u));
    this.api.setMyLanguage(language).subscribe();
  }

  login(): void {
    const returnUrl = window.location.pathname + window.location.search;
    window.location.href = '/bff/login?returnUrl=' + encodeURIComponent(returnUrl);
  }

  /** From the access-denied page: back to Entra ID, asking which account to use. */
  loginWithAnotherAccount(): void {
    window.location.href = '/bff/login?returnUrl=%2F&selectAccount=true';
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
