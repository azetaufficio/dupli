import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';
import { ProblemDetails } from './models';
import { ToastService } from './toast.service';

/** Set on a request whose errors the caller displays itself (e.g. form validation). */
export const SILENT_ERRORS = new HttpContextToken<boolean>(() => false);

export function problemMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as ProblemDetails | string | null;
    if (body && typeof body === 'object' && (body.detail || body.title))
      return body.detail ?? body.title!;
    if (typeof body === 'string' && body.length > 0 && body.length < 300) return body;
    if (error.status === 0) return 'Server unreachable';
    return `${error.status} ${error.statusText}`;
  }
  return error instanceof Error ? error.message : String(error);
}

export const httpErrorsInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const toasts = inject(ToastService);
  return next(req).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        !req.url.startsWith('/bff/') &&
        !auth.standalone()
      ) {
        auth.login();
      } else if (!req.context.get(SILENT_ERRORS)) {
        toasts.error(problemMessage(error));
      }
      return throwError(() => error);
    }),
  );
};
