import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';

export type ThemeMode = 'light' | 'dark' | 'system';
const KEY = 'tt.theme';

/** Light / Dark / System theme. System follows prefers-color-scheme and reacts live to OS changes. */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly doc = inject(DOCUMENT);
  private readonly media = globalThis.matchMedia?.('(prefers-color-scheme: dark)');
  private readonly osDark = signal(this.media?.matches ?? false);

  readonly mode = signal<ThemeMode>(this.read());
  readonly effective = computed<'light' | 'dark'>(() => {
    const m = this.mode();
    return m === 'system' ? (this.osDark() ? 'dark' : 'light') : m;
  });
  /** Bumped whenever effective theme changes, so token-reading charts can re-read colors. */
  readonly version = computed(() => this.effective());

  onChange?: (mode: ThemeMode) => void;

  constructor() {
    const listener = (e: MediaQueryListEvent) => {
      this.osDark.set(e.matches);
      this.apply();
    };
    this.media?.addEventListener?.('change', listener);
    inject(DestroyRef).onDestroy(() => this.media?.removeEventListener?.('change', listener));
  }

  init(): void { this.apply(); }

  set(mode: ThemeMode, persistRemote = true): void {
    this.mode.set(mode);
    try { localStorage.setItem(KEY, mode); } catch { /* ignore */ }
    this.apply();
    if (persistRemote) this.onChange?.(mode);
  }

  cycle(): void {
    const order: ThemeMode[] = ['light', 'dark', 'system'];
    this.set(order[(order.indexOf(this.mode()) + 1) % order.length]);
  }

  /** Reads a design token (e.g. '--tt-chart-1') from the root element. */
  token(name: string): string {
    return getComputedStyle(this.doc.documentElement).getPropertyValue(name).trim();
  }

  private apply(): void {
    const html = this.doc.documentElement;
    const eff = this.effective();
    html.setAttribute('data-theme', eff);
    html.style.colorScheme = eff;
  }

  private read(): ThemeMode {
    try {
      const v = localStorage.getItem(KEY);
      if (v === 'light' || v === 'dark' || v === 'system') return v;
    } catch { /* ignore */ }
    return 'system';
  }
}
