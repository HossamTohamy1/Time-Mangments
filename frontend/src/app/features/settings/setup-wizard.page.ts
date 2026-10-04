import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { InstitutionDto, TemplateSummaryDto } from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { afterSignIn } from '../../core/bootstrap';
import { ConfigStore } from '../../core/config/config.store';
import { LanguageService } from '../../core/i18n/language.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { LocalNamePipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';

/** Institution Setup Wizard: pick a template (or blank) → details → create. */
@Component({
  selector: 'app-setup-wizard-page',
  imports: [TranslocoDirective, ReactiveFormsModule, Icon, LocalNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="wizard" *transloco="let t">
      <div class="tt-card box">
        <div class="head">
          <span class="eyebrow tt-eyebrow">{{ t('settings.setup.eyebrow') }}</span>
          <h1>{{ t('settings.setup.title') }}</h1>
          <ol class="steps">
            <li [class.active]="step() === 1">1 · {{ t('settings.setup.stepTemplate') }}</li>
            <li [class.active]="step() === 2">2 · {{ t('settings.setup.stepDetails') }}</li>
          </ol>
        </div>
        @if (step() === 1) {
          <div class="templates" role="radiogroup">
            @for (tpl of templates(); track tpl.code) {
              <button type="button" role="radio" class="tpl" [attr.aria-checked]="selected() === tpl.code" (click)="selected.set(tpl.code)" [attr.data-testid]="'template-' + tpl.code">
                <strong>{{ tpl | localName }}</strong>
                <span class="tt-muted">{{ lang.pick(tpl.descriptionAr, tpl.descriptionEn) }}</span>
                @if (!tpl.isBuiltIn) { <span class="tt-chip">{{ t('settings.setup.custom') }}</span> }
              </button>
            }
          </div>
          <div class="tt-dialog-actions">
            @if (auth.institutions().length > 0) { <button type="button" class="tt-btn" (click)="cancel()">{{ t('common.cancel') }}</button> }
            <button type="button" class="tt-btn primary" [disabled]="!selected()" (click)="step.set(2)">{{ t('common.next') }} <app-icon name="arrowRight" /></button>
          </div>
        } @else {
          <form [formGroup]="form" (ngSubmit)="create()" class="details">
            <label class="tt-field">{{ t('fields.code') }} *<input class="tt-input" formControlName="code" dir="ltr" data-testid="setup-code" /></label>
            <label class="tt-field">{{ t('fields.nameEn') }}<input class="tt-input" formControlName="nameEn" dir="ltr" data-testid="setup-name-en" /></label>
            <label class="tt-field">{{ t('fields.nameAr') }}<input class="tt-input" formControlName="nameAr" dir="rtl" /></label>
            <label class="tt-field">{{ t('settings.setup.defaultLanguage') }}
              <select class="tt-input" formControlName="defaultLanguage"><option value="ar">العربية</option><option value="en">English</option></select>
            </label>
            @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
            <div class="tt-dialog-actions">
              <button type="button" class="tt-btn" (click)="step.set(1)"><app-icon name="arrowLeft" /> {{ t('common.back') }}</button>
              <button type="submit" class="tt-btn primary" [disabled]="busy() || form.invalid" data-testid="setup-create">{{ t('settings.setup.create') }}</button>
            </div>
          </form>
        }
      </div>
    </div>
  `,
  styles: [`
    .wizard { min-block-size: 100vh; display: grid; place-items: center; padding: 16px; background: var(--tt-bg); }
    .box { inline-size: min(860px, 100%); padding: 24px; }
    h1 { margin: 6px 0 10px; }
    .steps { display: flex; gap: 16px; list-style: none; padding: 0; margin: 0 0 16px; color: var(--tt-on-surface-muted); li.active { color: var(--tt-primary); font-weight: 600; } }
    .templates { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 10px; }
    .tpl { display: flex; flex-direction: column; gap: 6px; text-align: start; padding: 14px; border-radius: 10px; border: 1px solid var(--tt-border); background: var(--tt-surface);
      cursor: pointer; color: var(--tt-on-surface); &[aria-checked='true'] { border-color: var(--tt-primary); box-shadow: 0 0 0 2px var(--tt-primary-soft-border); } .tt-muted { font-size: 13px; } }
    .details { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; .tt-dialog-actions { grid-column: 1 / -1; } }
    .error { color: var(--tt-conflict); grid-column: 1 / -1; }
  `],
})
export class SetupWizardPage {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly config = inject(ConfigStore);
  private readonly realtime = inject(RealtimeService);
  protected readonly auth = inject(AuthStore);
  protected readonly lang = inject(LanguageService);

  protected readonly step = signal(1);
  protected readonly templates = signal<TemplateSummaryDto[]>([]);
  protected readonly selected = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$/)]],
    nameEn: [''], nameAr: [''], defaultLanguage: [this.lang.lang() as string],
  });

  constructor() {
    this.api.get<TemplateSummaryDto[]>('/templates').subscribe((t) => this.templates.set(t));
  }

  protected cancel(): void { void this.router.navigateByUrl('/settings'); }

  protected async create(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const v = this.form.getRawValue();
      const inst = await firstValueFrom(this.api.post<InstitutionDto>('/institutions', { ...v, templateCode: this.selected() }));
      await this.auth.switchInstitution(inst.id);
      await afterSignIn(this.auth, this.config, this.realtime);
      await this.router.navigateByUrl('/settings');
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : String(e));
    } finally {
      this.busy.set(false);
    }
  }
}
