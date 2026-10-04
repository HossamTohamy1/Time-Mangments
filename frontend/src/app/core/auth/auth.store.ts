import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, firstValueFrom, finalize, map, shareReplay, tap } from 'rxjs';
import { API_BASE, toApiError } from '../api/api';
import type { AuthResponse, MeResponse } from '../api/models';
import { LanguageService } from '../i18n/language.service';
import { ThemeService, ThemeMode } from '../theme/theme.service';

const INSTITUTION_KEY = 'tt.institution';
/** Non-sensitive hint that a refresh cookie probably exists (avoids a pointless refresh call for anonymous visitors). */
const SESSION_HINT = 'tt.session';

/** Session state: in-memory access token, refresh via HttpOnly cookie, current institution and permissions. */
@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly lang = inject(LanguageService);
  private readonly theme = inject(ThemeService);

  readonly accessToken = signal<string | null>(null);
  readonly me = signal<MeResponse | null>(null);
  readonly institutionId = signal<string | null>(readInstitution());
  readonly isAuthenticated = computed(() => !!this.accessToken() && !!this.me());
  readonly permissions = computed(() => new Set(this.me()?.permissions ?? []));
  readonly institutions = computed(() => this.me()?.institutions ?? []);
  readonly currentInstitution = computed(() => this.institutions().find((i) => i.institutionId === this.me()?.institutionId) ?? null);
  readonly displayName = computed(() => {
    const p = this.me()?.profile;
    return p ? this.lang.pick(p.displayNameAr, p.displayNameEn) || p.email : '';
  });

  private refreshing$: Observable<string> | null = null;

  constructor() {
    this.lang.onChange = (l) => { if (this.accessToken()) this.http.put(`${API_BASE}/auth/profile`, { preferredLanguage: l }).subscribe({ error: () => undefined }); };
    this.theme.onChange = (t) => { if (this.accessToken()) this.http.put(`${API_BASE}/auth/profile`, { preferredTheme: t }).subscribe({ error: () => undefined }); };
  }

  has(permission: string): boolean {
    return permission.split('|').some((p) => this.permissions().has(p));
  }

  async login(email: string, password: string): Promise<void> {
    try {
      const res = await firstValueFrom(this.http.post<AuthResponse>(`${API_BASE}/auth/login`, { email, password }));
      this.accessToken.set(res.accessToken);
      writeFlag(SESSION_HINT, true);
      await this.loadMe(true);
    } catch (e) {
      throw toApiError(e);
    }
  }

  /** Restores a session from the refresh cookie (called at bootstrap). */
  async tryRestore(): Promise<boolean> {
    if (!readFlag(SESSION_HINT)) return false;
    try {
      await firstValueFrom(this.refresh());
      await this.loadMe(false);
      return true;
    } catch {
      this.accessToken.set(null);
      return false;
    }
  }

  /** Single-flight token refresh shared by concurrent 401s. */
  refresh(): Observable<string> {
    this.refreshing$ ??= this.http.post<AuthResponse>(`${API_BASE}/auth/refresh`, {}).pipe(
      map((r) => r.accessToken),
      tap((t) => this.accessToken.set(t)),
      finalize(() => (this.refreshing$ = null)),
      shareReplay(1),
    );
    return this.refreshing$;
  }

  async loadMe(applyPreferences: boolean): Promise<void> {
    const me = await firstValueFrom(this.http.get<MeResponse>(`${API_BASE}/auth/me`));
    this.me.set(me);
    if (me.institutionId && me.institutionId !== this.institutionId()) this.setInstitution(me.institutionId);
    if (applyPreferences) {
      const p = me.profile;
      if (p.preferredLanguage === 'ar' || p.preferredLanguage === 'en') this.lang.set(p.preferredLanguage, false);
      if (['light', 'dark', 'system'].includes(p.preferredTheme)) this.theme.set(p.preferredTheme as ThemeMode, false);
      if (p.digitStyle === 'arabic-indic' || p.digitStyle === 'western') this.lang.setDigits(p.digitStyle);
    }
  }

  async switchInstitution(id: string): Promise<void> {
    this.setInstitution(id);
    await this.loadMe(false);
  }

  async logout(): Promise<void> {
    try { await firstValueFrom(this.http.post(`${API_BASE}/auth/logout`, {})); } catch { /* ignore */ }
    this.accessToken.set(null);
    this.me.set(null);
    writeFlag(SESSION_HINT, false);
    await this.router.navigate(['/login']);
  }

  /** Called by the interceptor when refresh fails: drop the session and go to login. */
  expire(): void {
    writeFlag(SESSION_HINT, false);
    this.accessToken.set(null);
    this.me.set(null);
    void this.router.navigate(['/login'], { queryParams: { expired: 1, returnUrl: this.router.url } });
  }

  private setInstitution(id: string): void {
    this.institutionId.set(id);
    try { localStorage.setItem(INSTITUTION_KEY, id); } catch { /* ignore */ }
  }
}

function readFlag(key: string): boolean {
  try { return localStorage.getItem(key) === '1'; } catch { return true; }
}

function writeFlag(key: string, on: boolean): void {
  try { if (on) localStorage.setItem(key, '1'); else localStorage.removeItem(key); } catch { /* ignore */ }
}

function readInstitution(): string | null {
  try { return localStorage.getItem(INSTITUTION_KEY); } catch { return null; }
}
