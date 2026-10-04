import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslocoDirective } from '@jsverse/transloco';
import { ApiError } from '../../core/api/api';
import { AuthStore } from '../../core/auth/auth.store';
import { afterSignIn } from '../../core/bootstrap';
import { ConfigStore } from '../../core/config/config.store';
import { LanguageService } from '../../core/i18n/language.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { ThemeService } from '../../core/theme/theme.service';
import { Icon } from '../../shared/ui/icon';

@Component({
  selector: 'app-login-page',
  imports: [ReactiveFormsModule, TranslocoDirective, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="login" *transloco="let t">
      <div class="top tt-row">
        <div class="tt-seg" role="group" [attr.aria-label]="t('shell.language')">
          <button type="button" [attr.aria-pressed]="lang.lang() === 'en'" (click)="lang.set('en')" lang="en">EN</button>
          <button type="button" [attr.aria-pressed]="lang.lang() === 'ar'" (click)="lang.set('ar')" lang="ar">AR</button>
        </div>
        <button type="button" class="tt-btn icon ghost" [attr.aria-label]="t('theme.' + theme.mode())" (click)="theme.cycle()">
          <app-icon [name]="theme.mode() === 'light' ? 'sun' : theme.mode() === 'dark' ? 'moon' : 'auto'" />
        </button>
      </div>
      <form class="card tt-card" [formGroup]="form" (ngSubmit)="submit()" novalidate>
        <div class="brand">
          <div class="logo"><app-icon name="calendar" [size]="24" /></div>
          <div>
            <h1>{{ t('app.name') }}</h1>
            <p class="tt-muted">{{ t('login.subtitle') }}</p>
          </div>
        </div>
        @if (expired()) { <p class="notice" role="status">{{ t('login.expired') }}</p> }
        <label class="tt-field">
          <span>{{ t('login.email') }}</span>
          <input class="tt-input" type="email" formControlName="email" autocomplete="username" dir="ltr" data-testid="login-email" />
        </label>
        <label class="tt-field">
          <span>{{ t('login.password') }}</span>
          <input class="tt-input" type="password" formControlName="password" autocomplete="current-password" dir="ltr" data-testid="login-password" />
        </label>
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        <button class="tt-btn primary submit" type="submit" [disabled]="busy() || form.invalid" data-testid="login-submit">
          {{ busy() ? t('login.signingIn') : t('login.signIn') }}
        </button>
        <p class="hint tt-muted">{{ t('login.demoHint') }}</p>
      </form>
    </div>
  `,
  styles: [`
    .login { min-block-size: 100vh; display: grid; place-items: center; padding: 16px; background: var(--tt-bg); position: relative; }
    .top { position: absolute; inset-block-start: 16px; inset-inline-end: 16px; }
    .card { inline-size: min(420px, 100%); padding: 28px; display: flex; flex-direction: column; gap: 14px; }
    .brand { display: flex; gap: 14px; align-items: center; margin-block-end: 8px; h1 { margin: 0; font-size: 22px; } p { margin: 0; } }
    .logo { inline-size: 48px; block-size: 48px; border-radius: 12px; display: grid; place-items: center; background: var(--tt-primary); color: var(--tt-on-primary); }
    .submit { justify-content: center; block-size: 42px; }
    .error { color: var(--tt-conflict); margin: 0; }
    .notice { background: var(--tt-info-bg); color: var(--tt-info); padding: 8px 10px; border-radius: 8px; margin: 0; }
    .hint { font-size: 12px; margin: 0; }
  `],
})
export class LoginPage {
  protected readonly lang = inject(LanguageService);
  protected readonly theme = inject(ThemeService);
  private readonly auth = inject(AuthStore);
  private readonly config = inject(ConfigStore);
  private readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly expired = signal(this.route.snapshot.queryParamMap.has('expired'));

  protected async submit(): Promise<void> {
    if (this.form.invalid) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const { email, password } = this.form.getRawValue();
      await this.auth.login(email, password);
      await afterSignIn(this.auth, this.config, this.realtime);
      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
      const home = !this.auth.me()?.institutionId ? '/setup' : this.auth.has('dashboard.view') ? '/dashboard' : '/my-timetable';
      await this.router.navigateByUrl(returnUrl && returnUrl !== '/login' ? returnUrl : home);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : String(e));
    } finally {
      this.busy.set(false);
    }
  }
}
