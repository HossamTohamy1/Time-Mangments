import { DOCUMENT } from '@angular/common';
import { Injectable, computed, inject, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';

export type AppLang = 'en' | 'ar';
export type DigitStyle = 'western' | 'arabic-indic';

const LANG_KEY = 'tt.lang';
const DIGITS_KEY = 'tt.digits';

function readStorage(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}
function writeStorage(key: string, value: string): void {
  try { localStorage.setItem(key, value); } catch { /* storage unavailable: keep in memory only */ }
}

/** Owns the active language, document direction and digit style. Switching never reloads the page. */
@Injectable({ providedIn: 'root' })
export class LanguageService {
  private readonly doc = inject(DOCUMENT);
  private readonly transloco = inject(TranslocoService);

  readonly lang = signal<AppLang>(this.initialLang());
  readonly dir = computed(() => (this.lang() === 'ar' ? 'rtl' : 'ltr'));
  readonly isRtl = computed(() => this.lang() === 'ar');
  readonly digits = signal<DigitStyle>((readStorage(DIGITS_KEY) as DigitStyle) || 'western');
  /** BCP-47 locale used for Intl formatting (dates, numbers). */
  readonly locale = computed(() => {
    const base = this.lang() === 'ar' ? 'ar-EG' : 'en';
    const nu = this.digits() === 'arabic-indic' ? 'arab' : 'latn';
    return `${base}-u-nu-${nu}`;
  });

  /** Hook used by the profile store to persist the choice server-side. */
  onChange?: (lang: AppLang) => void;

  init(): void {
    this.apply(this.lang());
  }

  set(lang: AppLang, persistRemote = true): void {
    if (lang !== 'en' && lang !== 'ar') return;
    this.lang.set(lang);
    writeStorage(LANG_KEY, lang);
    this.apply(lang);
    if (persistRemote) this.onChange?.(lang);
  }

  setDigits(style: DigitStyle): void {
    this.digits.set(style);
    writeStorage(DIGITS_KEY, style);
  }

  /** Picks the name in the active language, falling back to the other one. */
  pick(ar: string | null | undefined, en: string | null | undefined): string {
    const primary = this.lang() === 'ar' ? ar : en;
    const fallback = this.lang() === 'ar' ? en : ar;
    return (primary && primary.trim()) || (fallback && fallback.trim()) || '';
  }

  formatNumber(value: number, opts?: Intl.NumberFormatOptions): string {
    return new Intl.NumberFormat(this.locale(), opts).format(value);
  }

  formatDate(value: string | Date, opts: Intl.DateTimeFormatOptions = { dateStyle: 'medium' }): string {
    const d = typeof value === 'string' ? new Date(value) : value;
    return new Intl.DateTimeFormat(this.locale(), opts).format(d);
  }

  private apply(lang: AppLang): void {
    const html = this.doc.documentElement;
    html.lang = lang;
    html.dir = lang === 'ar' ? 'rtl' : 'ltr';
    this.transloco.setActiveLang(lang);
  }

  private initialLang(): AppLang {
    const saved = readStorage(LANG_KEY);
    if (saved === 'ar' || saved === 'en') return saved;
    const nav = (globalThis.navigator?.language ?? 'en').toLowerCase();
    return nav.startsWith('ar') ? 'ar' : 'en';
  }
}
