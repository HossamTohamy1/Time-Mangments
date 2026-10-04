import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError, PagedResult } from '../../core/api/api';
import type { OrgUnitDto } from '../../core/api/models';
import { ConfigStore } from '../../core/config/config.store';
import { ToastService } from '../../core/ui/toast.service';
import { EntitySchema } from '../../shared/forms/field-defs';
import { RefCache } from '../../shared/forms/ref-cache';
import { LocalNamePipe, LookupNamePipe, TermPipe } from '../../shared/pipes/pipes';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon } from '../../shared/ui/icon';
import { EntityFormDialog } from '../entities/entity-form.dialog';
import { UsageDialog, UsageItem } from '../entities/usage.dialog';

interface TreeRow { unit: OrgUnitDto; depth: number; hasChildren: boolean; }

const SCHEMA: EntitySchema = {
  key: 'org-units', endpoint: '/org-units', title: 'term:orgUnit', singular: 'term:orgUnit', icon: 'tree', managePermission: 'config.manage',
  fields: [
    { key: 'code', label: 'fields.code', type: 'text', required: true, dir: 'ltr' },
    { key: 'orgUnitTypeId', label: 'fields.level', type: 'lookup', lookup: 'org-unit-types', required: true },
    { key: 'nameEn', label: 'fields.nameEn', type: 'text', dir: 'ltr' },
    { key: 'nameAr', label: 'fields.nameAr', type: 'text', dir: 'rtl' },
    { key: 'parentId', label: 'fields.parent', type: 'ref', ref: '/org-units' },
    { key: 'sortOrder', label: 'fields.sortOrder', type: 'number', default: 0 },
  ],
  columns: [],
};

/** Org structure editor: arbitrary-depth tree whose levels are the institution's org-unit types. */
@Component({
  selector: 'app-org-structure-page',
  imports: [TranslocoDirective, Icon, LocalNamePipe, LookupNamePipe, TermPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head">
        <app-icon name="tree" [size]="22" class="head-icon" /><h1>{{ 'orgUnit' | term }}</h1>
        <div class="tt-spacer"></div>
        <button type="button" class="tt-btn primary" (click)="add(null)"><app-icon name="plus" /> {{ t('settings.org.addRoot') }}</button>
      </div>
      <div class="tt-card tree" role="tree">
        @for (r of tree(); track r.unit.id) {
          <div class="node" role="treeitem" aria-selected="false" [attr.aria-level]="r.depth + 1" [style.--depth]="r.depth" [attr.aria-expanded]="r.hasChildren ? !collapsed().has(r.unit.id) : null">
            @if (r.hasChildren) {
              <button type="button" class="twisty" (click)="toggle(r.unit.id)" [attr.aria-label]="t('common.toggle')">
                <app-icon [name]="collapsed().has(r.unit.id) ? 'chevronRight' : 'chevronDown'" />
              </button>
            } @else { <span class="twisty"></span> }
            <span class="tt-chip primary">{{ r.unit.orgUnitTypeId | lookupName: 'org-unit-types' }}</span>
            <strong class="code">{{ r.unit.code }}</strong>
            <span dir="auto">{{ r.unit | localName }}</span>
            <span class="tt-spacer"></span>
            <button type="button" class="tt-btn sm ghost" (click)="add(r.unit)"><app-icon name="plus" /> {{ t('settings.org.addChild') }}</button>
            <button type="button" class="tt-btn sm ghost icon" (click)="edit(r.unit)" [attr.aria-label]="t('common.edit')"><app-icon name="edit" /></button>
            <button type="button" class="tt-btn sm ghost icon" (click)="remove(r.unit)" [attr.aria-label]="t('common.delete')"><app-icon name="trash" /></button>
          </div>
        } @empty { <div class="tt-empty">{{ t('settings.org.empty') }}</div> }
      </div>
    </div>
  `,
  styles: [`
    .head-icon { color: var(--tt-primary); }
    .tree { padding: 8px; }
    .node { display: flex; align-items: center; gap: 8px; padding: 6px 8px; padding-inline-start: calc(8px + var(--depth) * 28px); border-radius: 8px;
      &:hover { background: var(--tt-surface-variant); } }
    .twisty { inline-size: 24px; block-size: 24px; display: grid; place-items: center; border: 0; background: transparent; cursor: pointer; color: var(--tt-on-surface-muted); }
    .code { min-inline-size: 70px; }
  `],
})
export class OrgStructurePage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly refs = inject(RefCache);
  protected readonly config = inject(ConfigStore);
  protected readonly units = signal<OrgUnitDto[]>([]);
  protected readonly collapsed = signal(new Set<string>());

  protected readonly tree = computed<TreeRow[]>(() => {
    const all = this.units();
    const byParent = new Map<string | null, OrgUnitDto[]>();
    for (const u of all) {
      const k = u.parentId ?? null;
      byParent.set(k, [...(byParent.get(k) ?? []), u]);
    }
    const out: TreeRow[] = [];
    const walk = (parent: string | null, depth: number) => {
      for (const u of (byParent.get(parent) ?? []).sort((a, b) => a.sortOrder - b.sortOrder || a.code.localeCompare(b.code))) {
        const kids = byParent.get(u.id) ?? [];
        out.push({ unit: u, depth, hasChildren: kids.length > 0 });
        if (!this.collapsed().has(u.id)) walk(u.id, depth + 1);
      }
    };
    walk(null, 0);
    return out;
  });

  constructor() { void this.load(); }

  async load(): Promise<void> {
    const res = await firstValueFrom(this.api.get<PagedResult<OrgUnitDto>>('/org-units', { pageSize: 500 }));
    this.units.set(res.items);
  }

  protected toggle(id: string): void {
    const s = new Set(this.collapsed());
    if (s.has(id)) s.delete(id); else s.add(id);
    this.collapsed.set(s);
  }

  async add(parent: OrgUnitDto | null): Promise<void> {
    const types = this.config.lookups('org-unit-types');
    const parentLevel = parent ? (this.config.lookup('org-unit-types', parent.orgUnitTypeId)?.extra?.['level'] as number | undefined) ?? 0 : 0;
    const nextType = types.find((t) => ((t.extra?.['level'] as number) ?? 0) > parentLevel) ?? types[0];
    await this.open(null, { parentId: parent?.id ?? null, orgUnitTypeId: nextType?.id ?? null, sortOrder: 0 });
  }

  async edit(u: OrgUnitDto): Promise<void> { await this.open(u as unknown as Record<string, unknown>); }

  private async open(value: Record<string, unknown> | null, defaults?: Record<string, unknown>): Promise<void> {
    const ref = this.dialog.open<Record<string, unknown>>(EntityFormDialog, { data: { schema: SCHEMA, value, defaults } });
    if (await firstValueFrom(ref.closed)) {
      this.toast.success('common.saved');
      this.refs.invalidate('/org-units');
      await this.load();
    }
  }

  async remove(u: OrgUnitDto): Promise<void> {
    if (!(await this.confirm.ask({ titleKey: 'common.deleteTitle', messageKey: 'common.deleteMessage', params: { name: u.code }, confirmKey: 'common.delete', danger: true }))) return;
    try {
      await firstValueFrom(this.api.delete(`/org-units/${u.id}`));
      this.refs.invalidate('/org-units');
      await this.load();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) this.dialog.open(UsageDialog, { data: { message: e.message, usages: e.problem.details as UsageItem[] } });
      else this.toast.error(e instanceof ApiError ? e.message : String(e));
    }
  }
}
