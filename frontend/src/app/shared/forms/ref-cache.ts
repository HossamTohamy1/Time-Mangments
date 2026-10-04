import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Api, PagedResult } from '../../core/api/api';
import { LanguageService } from '../../core/i18n/language.service';

export interface RefItem { id: string; code?: string; nameAr?: string | null; nameEn?: string | null; [k: string]: unknown; }

/** Caches small reference lists (rooms, instructors, groups…) for selects and label rendering. Invalidated on save. */
@Injectable({ providedIn: 'root' })
export class RefCache {
  private readonly api = inject(Api);
  private readonly lang = inject(LanguageService);
  private readonly lists = new Map<string, ReturnType<typeof signal<RefItem[]>>>();
  private readonly inflight = new Map<string, Promise<void>>();

  items(endpoint: string, query?: Record<string, string>): RefItem[] {
    const key = this.key(endpoint, query);
    let s = this.lists.get(key);
    if (!s) {
      s = signal<RefItem[]>([]);
      this.lists.set(key, s);
      void this.load(endpoint, query);
    }
    return s();
  }

  async load(endpoint: string, query?: Record<string, string>): Promise<void> {
    const key = this.key(endpoint, query);
    if (this.inflight.has(key)) return this.inflight.get(key);
    const p = (async () => {
      try {
        const page = await firstValueFrom(this.api.get<PagedResult<RefItem>>(endpoint, { pageSize: 500, ...(query ?? {}) }));
        let s = this.lists.get(key);
        if (!s) { s = signal<RefItem[]>([]); this.lists.set(key, s); }
        s.set(page.items);
      } finally {
        this.inflight.delete(key);
      }
    })();
    this.inflight.set(key, p);
    return p;
  }

  invalidate(endpoint: string): void {
    for (const k of [...this.lists.keys()]) if (k.startsWith(endpoint + '?') || k === endpoint) {
      const [ep, qs] = k.split('?');
      void this.load(ep, qs ? Object.fromEntries(new URLSearchParams(qs)) : undefined);
    }
  }

  label(endpoint: string, id: string | null | undefined, query?: Record<string, string>): string {
    if (!id) return '';
    const item = this.items(endpoint, query).find((i) => i.id === id);
    return item ? this.itemLabel(item) : '…';
  }

  itemLabel(item: RefItem): string {
    const name = this.lang.pick(item.nameAr, item.nameEn) || this.lang.pick(item['courseNameAr'] as string, item['courseNameEn'] as string);
    const code = item.code ?? (item['courseCode'] as string | undefined);
    return code && name ? `${code} · ${name}` : (name || code || item.id);
  }

  private key(endpoint: string, query?: Record<string, string>): string {
    const qs = query ? new URLSearchParams(query).toString() : '';
    return qs ? `${endpoint}?${qs}` : endpoint;
  }
}
