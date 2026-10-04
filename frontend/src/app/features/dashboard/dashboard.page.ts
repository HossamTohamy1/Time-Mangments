import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoDirective } from '@jsverse/transloco';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { LocalNamePipe, TermPipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';

/** Operations dashboard (KPIs and charts are completed in the dashboards phase). */
@Component({
  selector: 'app-dashboard-page',
  imports: [TranslocoDirective, RouterLink, Icon, LocalNamePipe, TermPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head">
        <h1>{{ t('dashboard.welcome', { name: auth.displayName() }) }}</h1>
      </div>
      <p class="tt-muted" dir="auto">{{ config.config()?.institution | localName }}</p>
      <div class="tt-grid-2">
        <a class="tt-card tile" routerLink="/instructors"><app-icon name="users" [size]="22" /> {{ 'instructors' | term }}</a>
        <a class="tt-card tile" routerLink="/rooms"><app-icon name="door" [size]="22" /> {{ t('entities.rooms') }}</a>
        <a class="tt-card tile" routerLink="/groups"><app-icon name="cap" [size]="22" /> {{ 'groups' | term }}</a>
        <a class="tt-card tile" routerLink="/settings"><app-icon name="gear" [size]="22" /> {{ t('nav.settings') }}</a>
      </div>
    </div>
  `,
  styles: [`.tile { display: flex; align-items: center; gap: 12px; padding: 18px; text-decoration: none; color: var(--tt-on-surface); font-weight: 600; app-icon { color: var(--tt-primary); } }`],
})
export class DashboardPage {
  protected readonly auth = inject(AuthStore);
  protected readonly config = inject(ConfigStore);
}
