import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Api } from '../api/api';
import type { ScheduleSummaryDto } from '../api/models';
import { AuthStore } from '../auth/auth.store';
import { RealtimeService } from '../realtime/realtime.service';
import { ShellStatus } from '../../layout/shell-status';

const KEY = 'tt.schedule';

function read(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}

function write(key: string, value: string | null): void {
  try { if (value) localStorage.setItem(key, value); else localStorage.removeItem(key); } catch { /* storage unavailable */ }
}

/**
 * The schedule the user is working on (editor, conflicts, exports). Remembered per institution;
 * defaults to the newest draft, then the published version.
 */
@Injectable({ providedIn: 'root' })
export class ScheduleContext {
  private readonly api = inject(Api);
  private readonly auth = inject(AuthStore);
  private readonly status = inject(ShellStatus);

  readonly schedules = signal<ScheduleSummaryDto[]>([]);
  readonly currentId = signal<string | null>(null);
  readonly loaded = signal(false);
  readonly current = computed(() => this.schedules().find((s) => s.id === this.currentId()) ?? null);
  readonly published = computed(() => this.schedules().find((s) => s.status === 'Published') ?? null);

  constructor() {
    effect(() => {
      const c = this.current();
      this.status.scheduleLabel.set(c ? c.name : null);
      this.status.conflictCount.set(c?.hardViolations ?? null);
    });
    inject(RealtimeService).scheduleChanged$.subscribe((e) => {
      if (['created', 'deleted', 'published', 'revalidated'].includes(e.kind)) void this.refresh();
    });
  }

  private storageKey(): string { return `${KEY}.${this.auth.institutionId() ?? ''}`; }

  async refresh(): Promise<void> {
    const list = await firstValueFrom(this.api.get<ScheduleSummaryDto[]>('/schedules'));
    this.schedules.set(list);
    this.loaded.set(true);
    const wanted = this.currentId() ?? read(this.storageKey());
    if (wanted && list.some((s) => s.id === wanted)) { this.currentId.set(wanted); return; }
    const draft = list.filter((s) => s.status === 'Draft').sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0];
    this.select((draft ?? list.find((s) => s.status === 'Published') ?? list[0])?.id ?? null);
  }

  async ensureLoaded(): Promise<void> {
    if (!this.loaded()) await this.refresh();
  }

  select(id: string | null): void {
    this.currentId.set(id);
    write(this.storageKey(), id);
  }

  /** Keeps list counters in sync after an edit or validation without reloading the list. */
  patch(id: string, change: Partial<ScheduleSummaryDto>): void {
    this.schedules.update((list) => list.map((s) => (s.id === id ? { ...s, ...change } : s)));
  }

  reset(): void {
    this.schedules.set([]);
    this.currentId.set(null);
    this.loaded.set(false);
  }
}
