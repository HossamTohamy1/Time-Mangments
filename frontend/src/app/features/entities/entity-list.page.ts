import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
import { debounceTime, distinctUntilChanged, firstValueFrom } from 'rxjs';
import { Api, ApiError, PagedResult } from '../../core/api/api';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { LanguageService } from '../../core/i18n/language.service';
import { ToastService } from '../../core/ui/toast.service';
import { ColumnDef, EntitySchema, FieldDef } from '../../shared/forms/field-defs';
import { RefCache } from '../../shared/forms/ref-cache';
import { DynamicFilterValue } from './entity-schemas';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon } from '../../shared/ui/icon';
import { LocalNamePipe, NumPipe, TermPipe } from '../../shared/pipes/pipes';
import { EntityFormDialog } from './entity-form.dialog';
import { UsageDialog, UsageItem } from './usage.dialog';

type Row = Record<string, unknown> & { id: string };

/** Generic, schema-driven list page: server paging, search (both names), sort, filters, custom-field columns, CRUD dialogs. */
@Component({
  selector: 'app-entity-list-page',
  imports: [TranslocoDirective, Icon, LocalNamePipe, NumPipe, TermPipe, RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './entity-list.page.html',
  styleUrl: './entity-list.page.scss',
})
export class EntityListPage {
  /** Bound from route data. */
  readonly schema = input.required<EntitySchema>();

  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);
  private readonly transloco = inject(TranslocoService);
  protected readonly auth = inject(AuthStore);
  protected readonly config = inject(ConfigStore);
  protected readonly refs = inject(RefCache);
  protected readonly lang = inject(LanguageService);

  protected readonly rows = signal<Row[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly pageSize = signal(25);
  protected readonly search = signal('');
  protected readonly sort = signal<string | null>(null);
  protected readonly desc = signal(false);
  protected readonly filters = signal<Record<string, DynamicFilterValue>>({});
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly canManage = computed(() => this.auth.has(this.schema().managePermission));
  protected readonly customColumns = computed(() =>
    this.schema().customFieldEntity ? this.config.customFields(this.schema().customFieldEntity!).filter((c) => c.showInTable) : []);
  protected readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize())));
  protected readonly links = computed(() => (this.schema().links ?? []).filter((l) => !l.feature || this.config.feature(l.feature)));
  protected readonly visibleFilters = computed(() => (this.schema().filters ?? []).filter((f) => !f.feature || this.config.feature(f.feature)));

  private readonly debouncedSearch = signal('');

  constructor() {
    toObservable(this.search).pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(inject(DestroyRef)))
      .subscribe((s) => { this.page.set(1); this.debouncedSearch.set(s); });
    effect(() => {
      // Reload whenever the query or institution changes.
      this.schema(); this.page(); this.pageSize(); this.debouncedSearch(); this.sort(); this.desc(); this.filters(); this.config.config();
      void this.load();
    });
  }

  protected title(): string {
    const t = this.schema().title;
    return t.startsWith('term:') ? this.config.term(t.slice(5)) : this.transloco.translate(t);
  }

  protected colLabel(c: ColumnDef): string {
    return c.label.startsWith('term:') ? this.config.term(c.label.slice(5)) : this.transloco.translate(c.label);
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const res = await firstValueFrom(this.api.get<PagedResult<Row>>(this.schema().endpoint, {
        page: this.page(), pageSize: this.pageSize(), search: this.debouncedSearch(), sort: this.sort() ?? this.schema().defaultSort, desc: this.desc(),
        ...this.filters(),
      }));
      this.rows.set(res.items);
      this.total.set(res.total);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : String(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected setSort(key: string): void {
    if (this.sort() === key) this.desc.set(!this.desc());
    else { this.sort.set(key); this.desc.set(false); }
  }

  protected setFilter(f: FieldDef, value: string): void {
    this.page.set(1);
    this.filters.update((x) => ({ ...x, [f.key]: value || undefined }));
  }

  protected filterOptions(f: FieldDef): { value: string; label: string }[] {
    if (f.lookup) return this.config.lookups(f.lookup).map((l) => ({ value: l.id, label: this.config.name(l) || l.code }));
    if (f.ref) return this.refs.items(f.ref).map((i) => ({ value: i.id, label: this.refs.itemLabel(i) }));
    return (f.options ?? []).map((o) => ({ value: String(o.value), label: this.transloco.translate(o.label) }));
  }

  protected cell(row: Row, c: ColumnDef): string {
    const v = row[c.key];
    switch (c.kind) {
      case 'lookup': return this.config.name(this.config.lookup(c.lookup!, v as string)) || '—';
      case 'ref': return v ? this.refs.label(c.ref!, v as string) : '—';
      case 'refMulti': return ((v as string[]) ?? []).map((id) => this.refs.label(c.ref!, id)).join(this.lang.isRtl() ? '، ' : ', ');
      case 'tags': return ((v as string[]) ?? []).join(', ');
      case 'lookupCodes': return ((v as string[]) ?? []).map((code) => this.config.name(this.config.lookupByCode(c.lookup!, code)) || code).join(this.lang.isRtl() ? '، ' : ', ');
      case 'bool': return v ? '✓' : '—';
      case 'number': return v === null || v === undefined ? '—' : this.lang.formatNumber(Number(v));
      case 'enum': return v === null || v === undefined ? '—' : this.transloco.translate(`${c.enumPrefix}.${v}`);
      case 'name': return this.lang.pick(row['nameAr'] as string, row['nameEn'] as string) || '—';
      default: return v === null || v === undefined || v === '' ? '—' : String(v);
    }
  }

  protected cfCell(row: Row, key: string): string {
    const v = (row['customFields'] as Record<string, unknown> | undefined)?.[key];
    if (v === undefined || v === null) return '—';
    if (Array.isArray(v)) return v.join(', ');
    if (typeof v === 'boolean') return v ? '✓' : '—';
    return String(v);
  }

  async openForm(row: Row | null): Promise<void> {
    const ref = this.dialog.open<Record<string, unknown>>(EntityFormDialog, { data: { schema: this.schema(), value: row }, maxWidth: 'calc(100vw - 32px)' });
    const saved = await firstValueFrom(ref.closed);
    if (saved) {
      this.toast.success(row ? 'common.saved' : 'common.created');
      this.refs.invalidate(this.schema().endpoint);
      await this.load();
    }
  }

  async remove(row: Row): Promise<void> {
    const label = (row['code'] as string) ?? this.lang.pick(row['nameAr'] as string, row['nameEn'] as string);
    if (!(await this.confirm.ask({ titleKey: 'common.deleteTitle', messageKey: 'common.deleteMessage', params: { name: label }, confirmKey: 'common.delete', danger: true }))) return;
    try {
      await firstValueFrom(this.api.delete(`${this.schema().endpoint}/${row.id}`));
      this.toast.success('common.deleted');
      this.refs.invalidate(this.schema().endpoint);
      await this.load();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && Array.isArray(e.problem.details)) {
        this.dialog.open(UsageDialog, { data: { message: e.message, usages: e.problem.details as UsageItem[] } });
      } else {
        this.toast.error(e instanceof ApiError ? e.message : String(e));
      }
    }
  }
}
