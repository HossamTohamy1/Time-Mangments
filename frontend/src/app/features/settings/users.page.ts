import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, PagedResult } from '../../core/api/api';
import type { RoleDto, UserListItemDto } from '../../core/api/models';
import { ToastService } from '../../core/ui/toast.service';
import { EntitySchema } from '../../shared/forms/field-defs';
import { RefCache } from '../../shared/forms/ref-cache';
import { LanguageService } from '../../core/i18n/language.service';
import { Icon } from '../../shared/ui/icon';
import { EntityFormDialog } from '../entities/entity-form.dialog';

/** User management: account, links to instructor / group (self-service), roles in this institution. */
@Component({
  selector: 'app-users-page',
  imports: [TranslocoDirective, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head">
        <app-icon name="users" [size]="22" class="head-icon" /><h1>{{ t('settings.users.title') }}</h1>
        <div class="tt-spacer"></div>
        <button type="button" class="tt-btn primary" (click)="edit(null)"><app-icon name="plus" /> {{ t('common.add') }}</button>
      </div>
      <div class="tt-card wrap">
        <table class="tt-table">
          <thead><tr><th>{{ t('fields.email') }}</th><th>{{ t('fields.name') }}</th><th>{{ t('settings.roles.title') }}</th><th>{{ t('fields.status') }}</th><th></th></tr></thead>
          <tbody>
            @for (u of users(); track u.id) {
              <tr>
                <td dir="ltr">{{ u.email }}</td>
                <td dir="auto">{{ displayName(u) }}</td>
                <td>@for (r of u.roles; track r.assignmentId) { <span class="tt-chip primary">{{ roleName(r.roleId) }}</span> }</td>
                <td>@if (u.isActive) { <span class="tt-chip valid">{{ t('common.active') }}</span> } @else { <span class="tt-chip">{{ t('common.inactive') }}</span> }</td>
                <td class="actions"><button type="button" class="tt-btn sm ghost icon" (click)="edit(u)" [attr.aria-label]="t('common.edit')"><app-icon name="edit" /></button></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
  styles: [`.head-icon { color: var(--tt-primary); } .wrap { overflow: auto; } .actions { text-align: end; } .tt-chip { margin-inline-end: 4px; }`],
})
export class UsersPage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toast = inject(ToastService);
  private readonly refs = inject(RefCache);
  private readonly lang = inject(LanguageService);
  protected readonly users = signal<UserListItemDto[]>([]);
  protected readonly roles = signal<RoleDto[]>([]);

  private readonly schema = computed<EntitySchema>(() => ({
    key: 'users', endpoint: '/users', title: 'settings.users.title', singular: 'User', icon: 'users', managePermission: 'users.manage',
    fields: [
      { key: 'email', label: 'fields.email', type: 'text', required: true, dir: 'ltr' },
      { key: 'password', label: 'fields.password', type: 'text', dir: 'ltr', hint: 'hints.password' },
      { key: 'displayNameEn', label: 'fields.nameEn', type: 'text', dir: 'ltr' },
      { key: 'displayNameAr', label: 'fields.nameAr', type: 'text', dir: 'rtl' },
      { key: 'preferredLanguage', label: 'shell.language', type: 'enum', default: 'en', required: true, options: [{ value: 'en', label: 'languages.en' }, { value: 'ar', label: 'languages.ar' }] },
      { key: 'isActive', label: 'fields.isActive', type: 'bool', default: true },
      { key: 'instructorId', label: 'term:instructor', type: 'ref', ref: '/instructors' },
      { key: 'studentGroupId', label: 'term:group', type: 'ref', ref: '/groups' },
      { key: 'roleIds', label: 'settings.roles.title', type: 'lookupMulti', options: this.roles().map((r) => ({ value: r.id, label: this.lang.pick(r.nameAr, r.nameEn) || r.code })) },
    ],
    columns: [],
  }));

  constructor() { void this.load(); }

  protected displayName(u: UserListItemDto): string { return this.lang.pick(u.displayNameAr, u.displayNameEn); }
  protected roleName(id: string): string { const r = this.roles().find((x) => x.id === id); return r ? this.lang.pick(r.nameAr, r.nameEn) || r.code : ''; }

  async load(): Promise<void> {
    const [users, roles] = await Promise.all([
      firstValueFrom(this.api.get<UserListItemDto[]>('/users')),
      firstValueFrom(this.api.get<PagedResult<RoleDto>>('/roles', { pageSize: 100 })),
    ]);
    this.users.set(users);
    this.roles.set(roles.items);
  }

  async edit(u: UserListItemDto | null): Promise<void> {
    const value = u ? { ...u, id: u.id, password: '', roleIds: u.roles.map((r) => r.roleId), preferredLanguage: 'en' } : null;
    const ref = this.dialog.open(EntityFormDialog, { data: { schema: this.schema(), value } });
    if (await firstValueFrom(ref.closed)) { this.toast.success('common.saved'); await this.load(); }
    void this.refs;
  }
}
