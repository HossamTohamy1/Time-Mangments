import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoDirective } from '@jsverse/transloco';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { Icon } from '../../shared/ui/icon';

interface Card { path: string; icon: string; key: string; permission: string; feature?: string; }

const CARDS: Card[] = [
  { path: 'transfer', icon: 'cap', key: 'institution', permission: 'config.manage' },
  { path: 'org-structure', icon: 'tree', key: 'orgStructure', permission: 'config.manage' },
  { path: 'lookups', icon: 'layers', key: 'lookups', permission: 'config.manage' },
  { path: 'time', icon: 'clock', key: 'time', permission: 'config.manage' },
  { path: 'curriculum', icon: 'book', key: 'curriculum', permission: 'config.manage', feature: 'curriculum' },
  { path: 'constraints', icon: 'shield', key: 'constraints', permission: 'rules.manage' },
  { path: 'terminology', icon: 'language', key: 'terminology', permission: 'config.manage' },
  { path: 'custom-fields', icon: 'tag', key: 'customFields', permission: 'config.manage', feature: 'custom-fields' },
  { path: 'roles', icon: 'key', key: 'roles', permission: 'roles.manage' },
  { path: 'users', icon: 'users', key: 'users', permission: 'users.manage' },
  { path: 'features', icon: 'flag', key: 'features', permission: 'config.manage' },
  { path: 'audit', icon: 'history', key: 'audit', permission: 'audit.view' },
];

@Component({
  selector: 'app-settings-home-page',
  imports: [TranslocoDirective, RouterLink, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head"><app-icon name="gear" [size]="22" class="head-icon" /><h1>{{ t('settings.title') }}</h1></div>
      <p class="tt-muted">{{ t('settings.subtitle') }}</p>
      <div class="cards">
        @for (c of cards(); track c.path) {
          <a class="tt-card card" [routerLink]="c.path">
            <span class="icon"><app-icon [name]="c.icon" [size]="20" /></span>
            <span class="text"><strong>{{ t('settings.' + c.key + '.title') }}</strong><span class="tt-muted">{{ t('settings.' + c.key + '.description') }}</span></span>
            <app-icon name="chevronRight" class="chev" />
          </a>
        }
      </div>
    </div>
  `,
  styles: [`
    .head-icon { color: var(--tt-primary); }
    .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 12px; }
    .card { display: flex; align-items: center; gap: 14px; padding: 16px; text-decoration: none; color: var(--tt-on-surface); transition: box-shadow .15s;
      &:hover { box-shadow: var(--tt-shadow-md); } }
    .icon { inline-size: 40px; block-size: 40px; border-radius: 10px; display: grid; place-items: center; background: var(--tt-primary-soft); color: var(--tt-primary); flex-shrink: 0; }
    .text { display: flex; flex-direction: column; gap: 2px; flex: 1; min-inline-size: 0; .tt-muted { font-size: 13px; } }
    .chev { color: var(--tt-on-surface-faint); }
  `],
})
export class SettingsHomePage {
  private readonly auth = inject(AuthStore);
  private readonly config = inject(ConfigStore);
  protected readonly cards = computed(() => CARDS.filter((c) => this.auth.has(c.permission) && (!c.feature || this.config.feature(c.feature))));
}
