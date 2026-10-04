import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError, PagedResult } from '../../core/api/api';
import type { RuleDto } from '../../core/api/models';
import { ToastService } from '../../core/ui/toast.service';
import { LocalNamePipe } from '../../shared/pipes/pipes';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon } from '../../shared/ui/icon';
import { RuleEditorDialog } from './rule-editor.dialog';

/** Custom no-code rules of the institution. */
@Component({
  selector: 'app-rule-builder-section',
  imports: [TranslocoDirective, Icon, LocalNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="tt-card pad" *transloco="let t">
      <div class="tt-row">
        <app-icon name="wand" [size]="20" class="accent" />
        <h2>{{ t('settings.rules.title') }}</h2>
        <span class="tt-spacer"></span>
        <button type="button" class="tt-btn primary" (click)="open(null)" data-testid="rule-add"><app-icon name="plus" /> {{ t('settings.rules.add') }}</button>
      </div>
      <p class="tt-muted">{{ t('settings.rules.subtitle') }}</p>
      @for (r of rules(); track r.id) {
        <div class="rule">
          <div class="info">
            <strong dir="auto">{{ r | localName }}</strong> <code>{{ r.code }}</code>
            <span class="tt-chip" [class.conflict]="r.severity === 'Hard'" [class.penalty]="r.severity === 'Soft'">{{ t('enums.severity.' + r.severity) }}</span>
            <span class="tt-muted small">{{ t('settings.rules.conditions.' + $any(r.definition)['condition']?.['type']) }}</span>
          </div>
          <button type="button" class="tt-btn sm ghost icon" (click)="open(r)" [attr.aria-label]="t('common.edit')"><app-icon name="edit" /></button>
          <button type="button" class="tt-btn sm ghost icon" (click)="remove(r)" [attr.aria-label]="t('common.delete')"><app-icon name="trash" /></button>
        </div>
      } @empty { <div class="tt-empty">{{ t('settings.rules.empty') }}</div> }
    </section>
  `,
  styles: [`.pad { padding: 16px; display: flex; flex-direction: column; gap: 8px; } h2 { margin: 0; font-size: 16px; } .accent { color: var(--tt-primary); }
    .rule { display: flex; align-items: center; gap: 8px; padding: 10px 12px; border: 1px solid var(--tt-border); border-radius: 10px; background: var(--tt-surface-variant); }
    .info { flex: 1; display: flex; flex-wrap: wrap; gap: 8px; align-items: center; } code { font-size: 12px; color: var(--tt-on-surface-muted); } .small { font-size: 12px; }`],
})
export class RuleBuilderSection {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  protected readonly rules = signal<RuleDto[]>([]);

  constructor() { void this.load(); }

  async load(): Promise<void> {
    this.rules.set((await firstValueFrom(this.api.get<PagedResult<RuleDto>>('/rules', { pageSize: 200 }))).items);
  }

  async open(rule: RuleDto | null): Promise<void> {
    const ref = this.dialog.open<boolean>(RuleEditorDialog, { data: { rule, scheduleId: null }, maxWidth: 'calc(100vw - 32px)' });
    if (await firstValueFrom(ref.closed)) { this.toast.success('common.saved'); await this.load(); }
  }

  async remove(r: RuleDto): Promise<void> {
    if (!(await this.confirm.ask({ titleKey: 'common.deleteTitle', messageKey: 'common.deleteMessage', params: { name: r.code }, confirmKey: 'common.delete', danger: true }))) return;
    try {
      await firstValueFrom(this.api.delete(`/rules/${r.id}`));
      await this.load();
    } catch (e) { this.toast.error(e instanceof ApiError ? e.message : String(e)); }
  }
}
