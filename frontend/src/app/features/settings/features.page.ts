import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import { ConfigStore } from '../../core/config/config.store';
import { ToastService } from '../../core/ui/toast.service';
import { Icon } from '../../shared/ui/icon';

/** Feature flags switch whole capabilities (menus, routes, endpoints and solver inputs follow). */
@Component({
  selector: 'app-features-page',
  imports: [TranslocoDirective, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head"><app-icon name="flag" [size]="22" class="head-icon" /><h1>{{ t('settings.features.title') }}</h1></div>
      <p class="tt-muted">{{ t('settings.features.subtitle') }}</p>
      <div class="tt-card list">
        @for (f of codes(); track f) {
          <label class="item" [attr.data-testid]="'feature-' + f">
            <span class="text"><strong>{{ t('features.' + f + '.name') }}</strong><span class="tt-muted">{{ t('features.' + f + '.description') }}</span></span>
            <input type="checkbox" role="switch" class="switch" [checked]="config.feature(f)" [disabled]="busy()" (change)="toggle(f, $any($event.target).checked)" />
          </label>
        }
      </div>
    </div>
  `,
  styles: [`.head-icon { color: var(--tt-primary); } .list { padding: 6px; }
    .item { display: flex; align-items: center; gap: 12px; padding: 12px; border-radius: 8px; cursor: pointer; &:hover { background: var(--tt-surface-variant); } }
    .text { display: flex; flex-direction: column; flex: 1; .tt-muted { font-size: 13px; } }
    .switch { appearance: none; inline-size: 40px; block-size: 22px; border-radius: 999px; background: var(--tt-border-strong); position: relative; cursor: pointer; transition: background .15s;
      &::after { content: ''; position: absolute; inset-block-start: 3px; inset-inline-start: 3px; inline-size: 16px; block-size: 16px; border-radius: 50%; background: var(--tt-surface); transition: inset-inline-start .15s; }
      &:checked { background: var(--tt-primary); &::after { inset-inline-start: 21px; } } }`],
})
export class FeaturesPage {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  protected readonly config = inject(ConfigStore);
  protected readonly busy = signal(false);
  protected codes(): string[] { return Object.keys(this.config.features()); }

  async toggle(code: string, enabled: boolean): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.put('/config/features', { [code]: enabled }));
      await this.config.load(true);
      this.toast.success('common.saved');
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : String(e));
    } finally {
      this.busy.set(false);
    }
  }
}
