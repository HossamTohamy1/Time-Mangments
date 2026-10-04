import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { API_BASE } from '../api/api';
import type { EffectiveConfigDto, LookupItemDto, CustomFieldDefinitionDto, CustomFieldEntity } from '../api/models';
import { LanguageService } from '../i18n/language.service';

export type LookupKind = 'session-types' | 'instructor-types' | 'room-types' | 'group-kinds' | 'org-unit-types' | 'equipment-tags';

/**
 * Effective configuration of the current institution (lookups, terminology, flags, permissions, time, custom
 * fields, constraint summary). Loaded at bootstrap with ETag and refreshed live on the SignalR ConfigChanged event.
 * Components render from here and never hard-code lookup values, colors, labels or menu items.
 */
@Injectable({ providedIn: 'root' })
export class ConfigStore {
  private readonly http = inject(HttpClient);
  private readonly lang = inject(LanguageService);
  private readonly transloco = inject(TranslocoService);
  private etag: string | null = null;

  readonly config = signal<EffectiveConfigDto | null>(null);
  readonly loaded = computed(() => this.config() !== null);
  readonly time = computed(() => this.config()?.time ?? null);
  readonly features = computed(() => this.config()?.features ?? {});
  readonly periods = computed(() => this.time()?.periods ?? []);
  readonly workingDays = computed(() => {
    const t = this.time();
    if (!t) return [] as number[];
    // Order the working days starting at the configured week start.
    return [...t.workingDays].sort((a, b) => ((a - t.weekStartDay + 7) % 7) - ((b - t.weekStartDay + 7) % 7));
  });
  readonly weekLabels = computed(() => {
    const t = this.time();
    if (!t || t.weekCycleLength <= 1) return [] as string[];
    return t.weekCycleLabels.length === t.weekCycleLength ? t.weekCycleLabels : Array.from({ length: t.weekCycleLength }, (_, i) => String(i + 1));
  });

  async load(force = false): Promise<void> {
    const headers: Record<string, string> = {};
    if (this.etag && !force) headers['If-None-Match'] = this.etag;
    try {
      const res = await firstValueFrom(this.http.get<EffectiveConfigDto>(`${API_BASE}/config/effective`, { headers, observe: 'response' }));
      this.apply(res);
    } catch (e: unknown) {
      if ((e as { status?: number }).status === 304) return;
      throw e;
    }
  }

  clear(): void { this.config.set(null); this.etag = null; }

  private apply(res: HttpResponse<EffectiveConfigDto>): void {
    if (res.status === 304 || !res.body) return;
    this.etag = res.headers.get('ETag');
    this.config.set(res.body);
  }

  feature(code: string): boolean { return this.features()[code] === true; }

  lookups(kind: LookupKind, activeOnly = true): LookupItemDto[] {
    const list = this.config()?.lookups?.[kind] ?? [];
    return activeOnly ? list.filter((l) => l.isActive) : list;
  }

  lookup(kind: LookupKind, id: string | null | undefined): LookupItemDto | undefined {
    if (!id) return undefined;
    return (this.config()?.lookups?.[kind] ?? []).find((l) => l.id === id);
  }

  lookupByCode(kind: LookupKind, code: string | null | undefined): LookupItemDto | undefined {
    if (!code) return undefined;
    return (this.config()?.lookups?.[kind] ?? []).find((l) => l.code === code);
  }

  name(item: { nameAr?: string | null; nameEn?: string | null } | null | undefined): string {
    return item ? this.lang.pick(item.nameAr, item.nameEn) : '';
  }

  customFields(entity: CustomFieldEntity): CustomFieldDefinitionDto[] {
    if (!this.feature('custom-fields')) return [];
    return (this.config()?.customFields ?? []).filter((f) => f.entityType === entity && f.isActive);
  }

  /**
   * Terminology: the institution's override for the active language first, then the translation key
   * `terms.<key>` (e.g. term('group') → "Section" for a university, "Class" for a school).
   */
  term(key: string): string {
    const lang = this.lang.lang();
    const overrides = this.config()?.terminology?.[lang];
    const override = overrides?.[`term.${key}`];
    if (override) return override;
    return this.transloco.translate(`terms.${key}`);
  }

  periodLabel(index: number): string {
    const p = this.periods().find((x) => x.index === index);
    return p ? this.lang.pick(p.nameAr, p.nameEn) || `${index + 1}` : `${index + 1}`;
  }
}
