import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError, PagedResult } from '../../core/api/api';
import type { RoleDto } from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { ToastService } from '../../core/ui/toast.service';
import { EntitySchema } from '../../shared/forms/field-defs';
import { LocalNamePipe } from '../../shared/pipes/pipes';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon } from '../../shared/ui/icon';
import { EntityFormDialog } from '../entities/entity-form.dialog';
import { UsageDialog, UsageItem } from '../entities/usage.dialog';

const ROLE_SCHEMA: EntitySchema = {
  key: 'roles', endpoint: '/roles', title: 'settings.roles.title', singular: 'Role', icon: 'key', managePermission: 'roles.manage',
  fields: [
    { key: 'code', label: 'fields.code', type: 'text', required: true, dir: 'ltr' },
    { key: 'nameEn', label: 'fields.nameEn', type: 'text', dir: 'ltr' },
    { key: 'nameAr', label: 'fields.nameAr', type: 'text', dir: 'rtl' },
  ],
  columns: [],
};

/** Roles × permissions matrix. Roles are data; the permission catalogue is code. */
@Component({
  selector: 'app-roles-page',
  imports: [TranslocoDirective, Icon, LocalNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head">
        <app-icon name="key" [size]="22" class="head-icon" /><h1>{{ t('settings.roles.title') }}</h1>
        <div class="tt-spacer"></div>
        <button type="button" class="tt-btn" (click)="addRole()"><app-icon name="plus" /> {{ t('settings.roles.add') }}</button>
        <button type="button" class="tt-btn primary" [disabled]="dirty().size === 0 || busy()" (click)="save()">{{ t('common.save') }}</button>
      </div>
      <div class="tt-card wrap">
        <table class="tt-table matrix">
          <thead>
            <tr>
              <th>{{ t('settings.roles.permission') }}</th>
              @for (r of roles(); track r.id) {
                <th class="role">
                  <span dir="auto">{{ r | localName }}</span>
                  <span class="tt-muted small">{{ t('settings.roles.members', { n: r.members }) }}</span>
                  <span class="role-actions">
                    <button type="button" class="tt-btn sm ghost icon" (click)="editRole(r)" [attr.aria-label]="t('common.edit')"><app-icon name="edit" /></button>
                    @if (!r.isSystem) { <button type="button" class="tt-btn sm ghost icon" (click)="deleteRole(r)" [attr.aria-label]="t('common.delete')"><app-icon name="trash" /></button> }
                  </span>
                </th>
              }
            </tr>
          </thead>
          <tbody>
            @for (g of groups(); track g[0]) {
              <tr class="group-row"><td [attr.colspan]="roles().length + 1">{{ t('permissions.groups.' + g[0]) }}</td></tr>
              @for (p of g[1]; track p) {
                <tr>
                  <td><span>{{ t('permissions.' + p.replaceAll('.', '_')) }}</span> <code class="tt-muted">{{ p }}</code></td>
                  @for (r of roles(); track r.id) {
                    <td class="cell"><input type="checkbox" [checked]="r.permissions.includes(p)" (change)="toggle(r, p)" [attr.aria-label]="(r | localName) + ': ' + t('permissions.' + p.replaceAll('.', '_'))" /></td>
                  }
                </tr>
              }
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
  styles: [`.head-icon { color: var(--tt-primary); } .wrap { overflow: auto; }
    .role { text-align: center; min-inline-size: 140px; span { display: block; } } .small { font-size: 11px; font-weight: 400; }
    .role-actions { display: flex !important; justify-content: center; }
    .cell { text-align: center; } .group-row td { background: var(--tt-surface-variant); font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: .06em; }
    code { font-size: 11px; }`],
})
export class RolesPage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly auth = inject(AuthStore);
  protected readonly roles = signal<RoleDto[]>([]);
  protected readonly groups = signal<[string, string[]][]>([]);
  protected readonly dirty = signal(new Set<string>());
  protected readonly busy = signal(false);

  constructor() { void this.load(); }

  async load(): Promise<void> {
    const [roles, catalogue] = await Promise.all([
      firstValueFrom(this.api.get<PagedResult<RoleDto>>('/roles', { pageSize: 100 })),
      firstValueFrom(this.api.get<{ groups: Record<string, string[]> }>('/config/permissions')),
    ]);
    this.roles.set(roles.items);
    this.groups.set(Object.entries(catalogue.groups));
    this.dirty.set(new Set());
  }

  protected toggle(r: RoleDto, p: string): void {
    this.roles.update((list) => list.map((x) => x.id !== r.id ? x : { ...x, permissions: x.permissions.includes(p) ? x.permissions.filter((y) => y !== p) : [...x.permissions, p] }));
    this.dirty.update((s) => new Set(s).add(r.id));
  }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      for (const r of this.roles().filter((x) => this.dirty().has(x.id)))
        await firstValueFrom(this.api.put(`/roles/${r.id}`, { code: r.code, nameAr: r.nameAr, nameEn: r.nameEn, permissions: r.permissions }));
      this.toast.success('common.saved');
      await Promise.all([this.load(), this.auth.loadMe(false)]);
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : String(e));
    } finally {
      this.busy.set(false);
    }
  }

  async addRole(): Promise<void> {
    const ref = this.dialog.open(EntityFormDialog, { data: { schema: ROLE_SCHEMA, value: null, defaults: { permissions: [] } } });
    if (await firstValueFrom(ref.closed)) await this.load();
  }

  async editRole(r: RoleDto): Promise<void> {
    const ref = this.dialog.open(EntityFormDialog, { data: { schema: { ...ROLE_SCHEMA, fields: [...ROLE_SCHEMA.fields, { key: 'permissions', label: 'settings.roles.permission', type: 'tags' }] }, value: r } });
    if (await firstValueFrom(ref.closed)) await this.load();
  }

  async deleteRole(r: RoleDto): Promise<void> {
    if (!(await this.confirm.ask({ titleKey: 'common.deleteTitle', messageKey: 'common.deleteMessage', params: { name: r.code }, confirmKey: 'common.delete', danger: true }))) return;
    try {
      await firstValueFrom(this.api.delete(`/roles/${r.id}`));
      await this.load();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) this.dialog.open(UsageDialog, { data: { message: e.message, usages: e.problem.details as UsageItem[] } });
      else this.toast.error(e instanceof ApiError ? e.message : String(e));
    }
  }
}
