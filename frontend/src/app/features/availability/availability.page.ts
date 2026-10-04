import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { AvailabilityCell, AvailabilityDto, AvailabilityState } from '../../core/api/models';
import { ConfigStore } from '../../core/config/config.store';
import { HasUnsavedChanges } from '../../core/guards/guards';
import { LanguageService } from '../../core/i18n/language.service';
import { ToastService } from '../../core/ui/toast.service';
import { RefCache } from '../../shared/forms/ref-cache';
import { DayNamePipe, LocalNamePipe, TermPipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';

type Target = 'instructors' | 'rooms';

/** Availability matrix for instructors and rooms: click-and-drag painting, keyboard painting, save. */
@Component({
  selector: 'app-availability-page',
  imports: [TranslocoDirective, Icon, DayNamePipe, LocalNamePipe, TermPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './availability.page.html',
  styleUrl: './availability.page.scss',
  host: { '(document:pointerup)': 'painting.set(false)' },
})
export class AvailabilityPage implements HasUnsavedChanges {
  readonly self = input(false);

  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  protected readonly config = inject(ConfigStore);
  protected readonly refs = inject(RefCache);
  protected readonly lang = inject(LanguageService);

  protected readonly target = signal<Target>('instructors');
  protected readonly selectedId = signal<string | null>(null);
  protected readonly cells = signal<Map<string, AvailabilityState>>(new Map());
  protected readonly brush = signal<AvailabilityState>('Unavailable');
  protected readonly painting = signal(false);
  protected readonly dirty = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly days = this.config.workingDays;
  protected readonly periods = this.config.periods;
  protected readonly items = computed(() => this.self() ? [] : this.refs.items(`/${this.target()}`));
  protected readonly brushes = computed<AvailabilityState[]>(() =>
    this.target() === 'rooms' && !this.self() ? ['Available', 'Unavailable'] : ['Available', 'Unavailable', 'Preferred']);

  constructor() {
    effect(() => {
      if (this.self()) { void this.load(); return; }
      const first = this.items()[0];
      if (!this.selectedId() && first) this.selectedId.set(first.id);
    });
    effect(() => { if (!this.self() && this.selectedId()) void this.load(); });
  }

  hasUnsavedChanges(): boolean { return this.dirty(); }

  private url(): string {
    return this.self() ? '/me/availability' : `/${this.target()}/${this.selectedId()}/availability`;
  }

  async load(): Promise<void> {
    this.error.set(null);
    try {
      const res = await firstValueFrom(this.api.get<AvailabilityDto>(this.url()));
      this.cells.set(new Map(res.cells.map((c) => [`${c.dayOfWeek}:${c.slotIndex}`, c.state])));
      this.dirty.set(false);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : String(e));
    }
  }

  protected switchTarget(t: Target): void {
    if (this.target() === t) return;
    this.target.set(t);
    this.selectedId.set(null);
    if (t === 'rooms' && this.brush() === 'Preferred') this.brush.set('Unavailable');
  }

  protected state(day: number, slot: number): AvailabilityState {
    return this.cells().get(`${day}:${slot}`) ?? 'Available';
  }

  protected disabled(day: number, slot: number): boolean {
    const p = this.periods().find((x) => x.index === slot);
    return !!p?.isBreak || !!this.config.time()?.dayOverrides.some((o) => o.dayOfWeek === day && o.slotIndex === slot && o.disabled);
  }

  protected paint(day: number, slot: number): void {
    if (this.disabled(day, slot)) return;
    const key = `${day}:${slot}`;
    const next = new Map(this.cells());
    if (this.brush() === 'Available') next.delete(key); else next.set(key, this.brush());
    this.cells.set(next);
    this.dirty.set(true);
  }

  protected start(day: number, slot: number, e: PointerEvent): void {
    e.preventDefault();
    this.painting.set(true);
    this.paint(day, slot);
  }

  protected enter(day: number, slot: number): void {
    if (this.painting()) this.paint(day, slot);
  }

  /** Arrow keys move in the visual direction (RTL-aware via DOM order); Space/Enter paints. */
  protected key(e: KeyboardEvent, day: number, slot: number): void {
    const di = this.days().indexOf(day);
    const rtl = this.lang.isRtl();
    let nd = di, ns = slot;
    switch (e.key) {
      case 'ArrowRight': nd = di + (rtl ? -1 : 1); break;
      case 'ArrowLeft': nd = di + (rtl ? 1 : -1); break;
      case 'ArrowDown': ns = slot + 1; break;
      case 'ArrowUp': ns = slot - 1; break;
      case ' ': case 'Enter': e.preventDefault(); this.paint(day, slot); return;
      default: return;
    }
    e.preventDefault();
    const d = this.days()[Math.max(0, Math.min(this.days().length - 1, nd))];
    const s = Math.max(0, Math.min(this.periods().length - 1, ns));
    (document.querySelector(`[data-cell="${d}:${s}"]`) as HTMLElement | null)?.focus();
  }

  protected fillAll(state: AvailabilityState): void {
    const next = new Map<string, AvailabilityState>();
    if (state !== 'Available')
      for (const d of this.days()) for (const p of this.periods()) if (!this.disabled(d, p.index)) next.set(`${d}:${p.index}`, state);
    this.cells.set(next);
    this.dirty.set(true);
  }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      const cells: AvailabilityCell[] = [...this.cells().entries()].map(([k, state]) => {
        const [d, s] = k.split(':').map(Number);
        return { dayOfWeek: d, slotIndex: s, state };
      });
      await firstValueFrom(this.api.put(this.url(), cells));
      this.dirty.set(false);
      this.toast.success('common.saved');
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : String(e));
    } finally {
      this.busy.set(false);
    }
  }
}
