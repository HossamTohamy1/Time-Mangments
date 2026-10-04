import { Injectable, signal } from '@angular/core';

export type ToastKind = 'success' | 'error' | 'info' | 'warning';

export interface Toast {
  id: number;
  kind: ToastKind;
  /** Server-provided (already localized) text… */
  text: string | null;
  /** …or a translation key used when text is null. */
  key: string | null;
  params?: Record<string, unknown>;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly toasts = signal<Toast[]>([]);
  private nextId = 1;

  success(key: string, params?: Record<string, unknown>): void { this.push('success', null, key, params); }
  info(key: string, params?: Record<string, unknown>): void { this.push('info', null, key, params); }
  warning(text: string | null, key: string | null = null): void { this.push('warning', text, key); }
  error(text: string | null, key: string | null = 'errors.unexpected'): void { this.push('error', text, key); }

  dismiss(id: number): void { this.toasts.update((t) => t.filter((x) => x.id !== id)); }

  private push(kind: ToastKind, text: string | null, key: string | null, params?: Record<string, unknown>): void {
    const id = this.nextId++;
    this.toasts.update((t) => [...t.slice(-4), { id, kind, text, key, params }]);
    setTimeout(() => this.dismiss(id), kind === 'error' ? 8000 : 4000);
  }
}
