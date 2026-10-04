import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthStore } from '../auth/auth.store';
import { LanguageService } from '../i18n/language.service';
import { ToastService } from '../ui/toast.service';
import { toApiError } from '../api/api';

/** Sends the active UI language so the server localizes messages, problem details and exports. */
export const languageInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api')) return next(req);
  const lang = inject(LanguageService).lang();
  return next(req.clone({ setHeaders: { 'Accept-Language': lang, 'X-Client-Language': lang } }));
};

function withAuth(req: HttpRequest<unknown>, token: string | null, institution: string | null) {
  const headers: Record<string, string> = {};
  if (token) headers['Authorization'] = `Bearer ${token}`;
  if (institution) headers['X-Institution-Id'] = institution;
  return req.clone({ setHeaders: headers });
}

const NO_REFRESH = ['/api/v1/auth/login', '/api/v1/auth/refresh', '/api/v1/auth/logout'];

/** Bearer token + institution header; on 401 refreshes once (single-flight) and retries. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api')) return next(req);
  const auth = inject(AuthStore);
  return next(withAuth(req, auth.accessToken(), auth.institutionId())).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse) || err.status !== 401 || NO_REFRESH.some((u) => req.url.startsWith(u))) return throwError(() => err);
      return auth.refresh().pipe(
        catchError((refreshErr) => { auth.expire(); return throwError(() => refreshErr); }),
        switchMap((token) => next(withAuth(req, token, auth.institutionId()))),
      );
    }),
  );
};

/** Network failures and unexpected 5xx errors raise a toast; 4xx are handled by the calling feature. */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const toast = inject(ToastService);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && (err.status === 0 || err.status >= 500)) {
        const e = toApiError(err);
        toast.error(err.status === 0 ? null : e.message, err.status === 0 ? 'errors.network' : 'errors.unexpected');
      }
      return throwError(() => err);
    }),
  );
};

export const appInterceptors: HttpInterceptorFn[] = [languageInterceptor, authInterceptor, errorInterceptor];
