import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import { AuthStore } from '../../core/auth/auth.store';
import { LanguageService, DigitStyle } from '../../core/i18n/language.service';
import { ThemeService, ThemeMode } from '../../core/theme/theme.service';
import { ToastService } from '../../core/ui/toast.service';

@Component({
  selector: 'app-profile-page',
  imports: [TranslocoDirective, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head"><h1>{{ t('profile.title') }}</h1></div>
      <section class="tt-card pad">
        <h2>{{ t('profile.preferences') }}</h2>
        <div class="tt-grid-2">
          <label class="tt-field">{{ t('shell.language') }}
            <select class="tt-input" [value]="lang.lang()" (change)="lang.set($any($event.target).value)">
              <option value="en">English</option><option value="ar">العربية</option>
            </select>
          </label>
          <label class="tt-field">{{ t('theme.label') }}
            <select class="tt-input" [value]="theme.mode()" (change)="theme.set($any($event.target).value)">
              @for (m of modes; track m) { <option [value]="m">{{ t('theme.' + m) }}</option> }
            </select>
          </label>
          <label class="tt-field">{{ t('profile.digits') }}
            <select class="tt-input" [value]="lang.digits()" (change)="setDigits($any($event.target).value)">
              <option value="western">{{ t('profile.digitsWestern') }}</option>
              <option value="arabic-indic">{{ t('profile.digitsArabic') }}</option>
            </select>
          </label>
        </div>
      </section>
      <form class="tt-card pad" [formGroup]="pw" (ngSubmit)="changePassword()">
        <h2>{{ t('profile.changePassword') }}</h2>
        <div class="tt-grid-2">
          <label class="tt-field">{{ t('profile.currentPassword') }}<input class="tt-input" type="password" formControlName="currentPassword" autocomplete="current-password" /></label>
          <label class="tt-field">{{ t('profile.newPassword') }}<input class="tt-input" type="password" formControlName="newPassword" autocomplete="new-password" /></label>
        </div>
        @if (pwError()) { <p class="error" role="alert">{{ pwError() }}</p> }
        <div class="tt-dialog-actions"><button class="tt-btn primary" type="submit" [disabled]="pw.invalid">{{ t('common.save') }}</button></div>
      </form>
    </div>
  `,
  styles: [`.pad { padding: 20px; } h2 { margin: 0 0 12px; font-size: 16px; } .error { color: var(--tt-conflict); }`],
})
export class ProfilePage {
  protected readonly lang = inject(LanguageService);
  protected readonly theme = inject(ThemeService);
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  protected readonly auth = inject(AuthStore);
  protected readonly modes: ThemeMode[] = ['light', 'dark', 'system'];
  protected readonly pw = inject(FormBuilder).nonNullable.group({ currentPassword: ['', Validators.required], newPassword: ['', [Validators.required, Validators.minLength(8)]] });
  protected readonly pwError = signal<string | null>(null);

  protected setDigits(d: DigitStyle): void {
    this.lang.setDigits(d);
    this.api.put('/auth/profile', { digitStyle: d }).subscribe({ error: () => undefined });
  }

  protected async changePassword(): Promise<void> {
    this.pwError.set(null);
    try {
      await firstValueFrom(this.api.post('/auth/change-password', this.pw.getRawValue()));
      this.pw.reset();
      this.toast.success('profile.passwordChanged');
    } catch (e) {
      this.pwError.set(e instanceof ApiError ? e.message : String(e));
    }
  }
}
