import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError, PagedResult } from '../../core/api/api';
import type { LookupDto } from '../../core/api/models';
import { ConfigStore, LookupKind } from '../../core/config/config.store';
import { ThemeService } from '../../core/theme/theme.service';
import { ToastService } from '../../core/ui/toast.service';
import { EntitySchema, FieldDef } from '../../shared/forms/field-defs';
import { NumPipe } from '../../shared/pipes/pipes';
import { accessible, deriveColors } from '../../shared/ui/color';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon } from '../../shared/ui/icon';
import { EntityFormDialog } from '../entities/entity-form.dialog';
import { UsageDialog, UsageDecision, UsageItem } from '../entities/usage.dialog';
import { MergeDialog } from './merge.dialog';
import { ImpactService } from '../../shared/impact/impact';

const KINDS: LookupKind[] = ['session-types', 'instructor-types', 'room-types', 'group-kinds', 'org-unit-types', 'equipment-tags'];

const COMMON: FieldDef[] = [
  { key: 'code', label: 'fields.code', type: 'text', required: true, dir: 'ltr' },
  { key: 'nameEn', label: 'fields.nameEn', type: 'text', dir: 'ltr' },
  { key: 'nameAr', label: 'fields.nameAr', type: 'text', dir: 'rtl' },
  { key: 'color', label: 'fields.color', type: 'color', hint: 'hints.color' },
  { key: 'icon', label: 'fields.icon', type: 'text', dir: 'ltr' },
  { key: 'sortOrder', label: 'fields.sortOrder', type: 'number', default: 0 },
  { key: 'isActive', label: 'fields.isActive', type: 'bool', default: true },
];

const EXTRA: Partial<Record<LookupKind, FieldDef[]>> = {
  'session-types': [
    { key: 'defaultDurationSlots', label: 'fields.defaultDurationSlots', type: 'number', min: 1, max: 12, default: 1 },
    { key: 'defaultRoomTypeId', label: 'fields.defaultRoomType', type: 'lookup', lookup: 'room-types' },
    { key: 'loadMultiplier', label: 'fields.loadMultiplier', type: 'number', min: 0, max: 10, step: 0.1, default: 1 },
    { key: 'canBeShared', label: 'fields.canBeShared', type: 'bool' },
    { key: 'countsTowardLoad', label: 'fields.countsTowardLoad', type: 'bool', default: true },
    { key: 'requiresInstructor', label: 'fields.requiresInstructor', type: 'bool', default: true },
    { key: 'requiresRoom', label: 'fields.requiresRoom', type: 'bool', default: true },
    { key: 'allowedInstructorTypeCodes', label: 'fields.allowedInstructorTypes', type: 'lookupMulti', lookup: 'instructor-types', valueBy: 'code' },
    { key: 'allowedDays', label: 'fields.allowedDays', type: 'days' },
    { key: 'allowedSlotFrom', label: 'fields.allowedSlotFrom', type: 'enum', optionsSource: 'periods' },
    { key: 'allowedSlotTo', label: 'fields.allowedSlotTo', type: 'enum', optionsSource: 'periods' },
  ],
  'instructor-types': [{ key: 'defaultMaxHoursPerWeek', label: 'fields.maxHoursPerWeek', type: 'number', min: 0, max: 168 }],
  'org-unit-types': [{ key: 'level', label: 'fields.level', type: 'number', min: 1, max: 20, default: 1 }],
};

/** Lookups manager: all configurable "types" are data (create, rename, hide, merge — never hard-coded). */
@Component({
  selector: 'app-lookups-page',
  imports: [TranslocoDirective, Icon, NumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head">
        <app-icon name="layers" [size]="22" class="head-icon" /><h1>{{ t('settings.lookups.title') }}</h1>
        <div class="tt-spacer"></div>
        <button type="button" class="tt-btn primary" (click)="edit(null)"><app-icon name="plus" /> {{ t('common.add') }}</button>
      </div>
      <div class="tabs" role="tablist">
        @for (k of kinds; track k) {
          <button type="button" role="tab" class="tab" [attr.aria-selected]="kind() === k" (click)="kind.set(k)">{{ t('settings.lookups.kinds.' + k) }}</button>
        }
      </div>
      <div class="tt-card table-wrap">
        <table class="tt-table">
          <thead><tr>
            <th>{{ t('fields.color') }}</th><th>{{ t('fields.code') }}</th><th>{{ t('fields.nameEn') }}</th><th>{{ t('fields.nameAr') }}</th>
            <th>{{ t('settings.lookups.usages') }}</th><th>{{ t('fields.status') }}</th><th><span class="tt-visually-hidden">{{ t('common.actions') }}</span></th>
          </tr></thead>
          <tbody>
            @for (row of rows(); track row.id) {
              <tr [class.inactive]="!row.isActive">
                <td>
                  @if (row.color) {
                    <span class="sample" [style.background]="colors(row.color).background" [style.color]="colors(row.color).text" [style.border-inline-start-color]="colors(row.color).accent">{{ row.code }}</span>
                    @if (!isAccessible(row.color)) { <span class="tt-chip penalty" [title]="t('settings.lookups.lowContrast')"><app-icon name="alert" /> AA</span> }
                  }
                </td>
                <td class="code">{{ row.code }} @if (row.isSystem) { <span class="tt-chip">{{ t('settings.lookups.system') }}</span> }</td>
                <td dir="ltr">{{ row.nameEn }}</td><td dir="rtl">{{ row.nameAr }}</td>
                <td>{{ row.usages | num }}</td>
                <td>@if (row.isActive) { <span class="tt-chip valid">{{ t('common.active') }}</span> } @else { <span class="tt-chip">{{ t('common.inactive') }}</span> }</td>
                <td class="actions">
                  <button type="button" class="tt-btn sm ghost icon" (click)="edit(row)" [attr.aria-label]="t('common.edit')"><app-icon name="edit" /></button>
                  <button type="button" class="tt-btn sm ghost icon" (click)="remove(row)" [attr.aria-label]="t('common.delete')"><app-icon name="trash" /></button>
                </td>
              </tr>
            } @empty { <tr><td colspan="7" class="tt-empty">{{ t('common.empty') }}</td></tr> }
          </tbody>
        </table>
      </div>
    </div>
  `,
  styles: [`
    .head-icon { color: var(--tt-primary); }
    .tabs { display: flex; gap: 4px; flex-wrap: wrap; border-block-end: 1px solid var(--tt-border); }
    .tab { border: 0; background: transparent; padding: 10px 14px; cursor: pointer; color: var(--tt-on-surface-muted); font-weight: 500; border-block-end: 2px solid transparent;
      &[aria-selected='true'] { color: var(--tt-primary); border-block-end-color: var(--tt-primary); } }
    .table-wrap { overflow: auto; }
    .code { font-weight: 600; }
    .sample { display: inline-block; padding: 2px 8px; border-radius: 6px; border-inline-start: 4px solid; font-size: 12px; font-weight: 600; }
    .inactive td { opacity: .6; }
    .actions { text-align: end; white-space: nowrap; }
  `],
})
export class LookupsPage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly theme = inject(ThemeService);
  private readonly impact = inject(ImpactService);
  protected readonly config = inject(ConfigStore);
  protected readonly kinds = KINDS;
  protected readonly kind = signal<LookupKind>('session-types');
  protected readonly rows = signal<LookupDto[]>([]);

  private readonly schema = computed<EntitySchema>(() => ({
    key: this.kind(), endpoint: `/lookups/${this.kind()}`, title: '', singular: '', icon: 'layers', managePermission: 'config.manage',
    fields: [...COMMON, ...(EXTRA[this.kind()] ?? [])], columns: [],
  }));

  constructor() {
    effect(() => { this.kind(); void this.load(); });
  }

  protected colors(c: string) { return deriveColors(c, this.theme.effective()); }
  protected isAccessible(c: string) { return accessible(c); }

  async load(): Promise<void> {
    const res = await firstValueFrom(this.api.get<PagedResult<LookupDto>>(`/lookups/${this.kind()}`, { pageSize: 500 }));
    this.rows.set(res.items);
  }

  async edit(row: LookupDto | null): Promise<void> {
    const value = row ? { ...row, allowedSlotFrom: row.allowedSlotFrom ?? null, allowedSlotTo: row.allowedSlotTo ?? null } as Record<string, unknown> : null;
    // Session-type behaviour changes (duration, allowed days/slots) go through impact analysis first.
    const beforeSave = this.kind() === 'session-types'
      ? (body: Record<string, unknown>, id: string | undefined) => id ? this.impact.confirm(`/lookups/session-types/${id}/impact`, body) : Promise.resolve(true)
      : undefined;
    const ref = this.dialog.open<Record<string, unknown>>(EntityFormDialog, { data: { schema: this.schema(), value, beforeSave } });
    if (await firstValueFrom(ref.closed)) {
      this.toast.success('common.saved');
      await Promise.all([this.load(), this.config.load(true)]);
    }
  }

  async remove(row: LookupDto): Promise<void> {
    if (!(await this.confirm.ask({ titleKey: 'common.deleteTitle', messageKey: 'common.deleteMessage', params: { name: row.code }, confirmKey: 'common.delete', danger: true }))) return;
    try {
      await firstValueFrom(this.api.delete(`/lookups/${this.kind()}/${row.id}`));
      this.toast.success('common.deleted');
      await Promise.all([this.load(), this.config.load(true)]);
    } catch (e) {
      if (!(e instanceof ApiError) || e.status !== 409) { this.toast.error(e instanceof ApiError ? e.message : String(e)); return; }
      const decision = await firstValueFrom(this.dialog.open<UsageDecision>(UsageDialog, {
        data: { message: e.message, usages: (e.problem.details as UsageItem[]) ?? [], canMerge: true, canDeactivate: row.isActive },
      }).closed);
      if (decision === 'deactivate') {
        await firstValueFrom(this.api.put(`/lookups/${this.kind()}/${row.id}`, { ...row, isActive: false }));
        this.toast.success('settings.lookups.deactivated');
      } else if (decision === 'merge') {
        const targetId = await firstValueFrom(this.dialog.open<string>(MergeDialog, { data: { source: row, options: this.rows().filter((r) => r.id !== row.id && r.isActive) } }).closed);
        if (!targetId) return;
        await firstValueFrom(this.api.post(`/lookups/${this.kind()}/${row.id}/merge`, { targetId }));
        this.toast.success('settings.lookups.merged');
      }
      await Promise.all([this.load(), this.config.load(true)]);
    }
  }
}
