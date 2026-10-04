import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { LanguageService } from '../../core/i18n/language.service';
import { ThemeService } from '../../core/theme/theme.service';

@Component({
  selector: 'app-home-page',
  imports: [TranslocoDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="tt-page" *transloco="let t">
      <h1>{{ t('app.name') }}</h1>
      <p class="tt-muted">{{ t('app.tagline') }}</p>
      <div class="tt-row">
        <div class="tt-seg" role="group" [attr.aria-label]="t('shell.language')">
          <button type="button" [attr.aria-pressed]="lang.lang() === 'en'" (click)="lang.set('en')">EN</button>
          <button type="button" [attr.aria-pressed]="lang.lang() === 'ar'" (click)="lang.set('ar')">AR</button>
        </div>
        <button type="button" class="tt-btn" (click)="theme.cycle()">{{ t('theme.' + theme.mode()) }}</button>
      </div>
    </main>
  `,
})
export class HomePage {
  protected readonly lang = inject(LanguageService);
  protected readonly theme = inject(ThemeService);
}
