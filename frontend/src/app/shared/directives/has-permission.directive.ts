import { Directive, TemplateRef, ViewContainerRef, effect, inject, input } from '@angular/core';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';

/** *hasPermission="'timetable.edit'" (use "a|b" for any-of). Re-evaluates when permissions change. */
@Directive({ selector: '[hasPermission]' })
export class HasPermissionDirective {
  private readonly auth = inject(AuthStore);
  private readonly tpl = inject(TemplateRef<unknown>);
  private readonly vcr = inject(ViewContainerRef);
  readonly hasPermission = input.required<string>();
  private shown = false;

  constructor() {
    effect(() => {
      const ok = this.auth.has(this.hasPermission());
      if (ok && !this.shown) { this.vcr.createEmbeddedView(this.tpl); this.shown = true; }
      else if (!ok && this.shown) { this.vcr.clear(); this.shown = false; }
    });
  }
}

/** *ifFeature="'substitutions'" — renders only when the institution's feature flag is on. */
@Directive({ selector: '[ifFeature]' })
export class IfFeatureDirective {
  private readonly config = inject(ConfigStore);
  private readonly tpl = inject(TemplateRef<unknown>);
  private readonly vcr = inject(ViewContainerRef);
  readonly ifFeature = input.required<string>();
  private shown = false;

  constructor() {
    effect(() => {
      const ok = this.config.feature(this.ifFeature());
      if (ok && !this.shown) { this.vcr.createEmbeddedView(this.tpl); this.shown = true; }
      else if (!ok && this.shown) { this.vcr.clear(); this.shown = false; }
    });
  }
}
