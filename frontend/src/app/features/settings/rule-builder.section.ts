import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';

/** Rule Builder (no-code rules). Full editor, live preview and impact are part of the constraint-engine phase. */
@Component({
  selector: 'app-rule-builder-section',
  imports: [TranslocoDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<section class="tt-card" style="padding:16px" *transloco="let t"><h2 style="margin:0;font-size:16px">{{ t('settings.rules.title') }}</h2></section>`,
})
export class RuleBuilderSection {}
