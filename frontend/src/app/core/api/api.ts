import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';

export const API_BASE = '/api/v1';

export interface FieldProblem { code: string; message: string; params?: Record<string, unknown>; }

/** RFC 7807 problem as returned by the API (stable code + localized message + structured params). */
export interface ApiProblem {
  status: number;
  code: string;
  message: string;
  params?: Record<string, string>;
  errors?: Record<string, FieldProblem[]>;
  details?: unknown;
  traceId?: string;
}

export class ApiError extends Error {
  constructor(readonly problem: ApiProblem) {
    super(problem.message || problem.code);
  }
  get status() { return this.problem.status; }
  get code() { return this.problem.code; }
}

export function toApiError(err: unknown): ApiError {
  if (err instanceof ApiError) return err;
  if (err instanceof HttpErrorResponse) {
    const body = (err.error ?? {}) as Partial<ApiProblem> & { title?: string };
    return new ApiError({
      status: err.status,
      code: body.code ?? (err.status === 0 ? 'NETWORK_ERROR' : `HTTP_${err.status}`),
      message: body.message ?? body.title ?? err.message,
      params: body.params,
      errors: body.errors,
      details: body.details,
      traceId: body.traceId,
    });
  }
  return new ApiError({ status: 0, code: 'UNEXPECTED_ERROR', message: String(err) });
}

export type QueryValue = string | number | boolean | null | undefined;

export interface PagedResult<T> { items: T[]; total: number; page: number; pageSize: number; }

/** Thin typed HTTP client over the versioned API; DTO types come from the generated OpenAPI models. */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  get<T>(path: string, query?: Record<string, QueryValue>): Observable<T> {
    return this.http.get<T>(API_BASE + path, { params: this.params(query) }).pipe(catchError((e) => throwError(() => toApiError(e))));
  }

  post<T>(path: string, body: unknown = {}, query?: Record<string, QueryValue>): Observable<T> {
    return this.http.post<T>(API_BASE + path, body, { params: this.params(query) }).pipe(catchError((e) => throwError(() => toApiError(e))));
  }

  put<T>(path: string, body: unknown): Observable<T> {
    return this.http.put<T>(API_BASE + path, body).pipe(catchError((e) => throwError(() => toApiError(e))));
  }

  delete<T = void>(path: string): Observable<T> {
    return this.http.delete<T>(API_BASE + path).pipe(catchError((e) => throwError(() => toApiError(e))));
  }

  download(path: string, query?: Record<string, QueryValue>): Observable<Blob> {
    return this.http.get(API_BASE + path, { params: this.params(query), responseType: 'blob' }).pipe(catchError((e) => throwError(() => toApiError(e))));
  }

  upload<T>(path: string, form: FormData, query?: Record<string, QueryValue>): Observable<T> {
    return this.http.post<T>(API_BASE + path, form, { params: this.params(query) }).pipe(catchError((e) => throwError(() => toApiError(e))));
  }

  private params(query?: Record<string, QueryValue>): HttpParams {
    let p = new HttpParams();
    for (const [k, v] of Object.entries(query ?? {})) if (v !== undefined && v !== null && v !== '') p = p.set(k, String(v));
    return p;
  }
}

/** Saves a blob as a file in the browser. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
