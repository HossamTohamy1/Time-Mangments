import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoDirective } from '@jsverse/transloco';
import { Icon } from '../../shared/ui/icon';

@Component({
  selector: 'app-forbidden-page',
  imports: [TranslocoDirective, RouterLink, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-card tt-empty">
        <app-icon name="shield" [size]="40" />
        <h1>{{ t('errors.forbiddenTitle') }}</h1>
        <p>{{ t('errors.forbiddenText') }}</p>
        <a class="tt-btn primary" routerLink="/">{{ t('common.goHome') }}</a>
      </div>
    </div>
  `,
})
export class ForbiddenPage {}
