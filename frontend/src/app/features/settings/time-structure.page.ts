import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { DayOverrideDto, PeriodDto, ShiftDto, TimeStructureDto } from '../../core/api/models';
import { ConfigStore } from '../../core/config/config.store';
import { HasUnsavedChanges } from '../../core/guards/guards';
import { ToastService } from '../../core/ui/toast.service';
import { DayNamePipe, LocalNamePipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';

/** Time structure: working days, week start, N-week cycle, named periods (non-uniform), breaks, shifts, per-day overrides. */
@Component({
  selector: 'app-time-structure-page',
  imports: [TranslocoDirective, FormsModule, Icon, DayNamePipe, LocalNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './time-structure.page.html',
  styleUrl: './time-structure.page.scss',
})
export class TimeStructurePage implements HasUnsavedChanges {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  protected readonly config = inject(ConfigStore);

  protected readonly model = signal<TimeStructureDto | null>(null);
  protected readonly dirty = signal(false);
  protected readonly busy = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly days = [0, 1, 2, 3, 4, 5, 6];

  constructor() {
    effect(() => {
      const t = this.config.time();
      if (t && !this.dirty()) this.model.set(structuredClone(t));
    });
  }

  hasUnsavedChanges(): boolean { return this.dirty(); }

  private update(fn: (m: TimeStructureDto) => void): void {
    const m = structuredClone(this.model()!);
    fn(m);
    this.model.set(m);
    this.dirty.set(true);
  }

  protected toggleDay(d: number): void {
    this.update((m) => { m.workingDays = m.workingDays.includes(d) ? m.workingDays.filter((x) => x !== d) : [...m.workingDays, d]; });
  }
  protected setWeekStart(d: number): void { this.update((m) => { m.weekStartDay = Number(d); }); }
  protected setCycle(n: number): void {
    this.update((m) => {
      m.weekCycleLength = Math.max(1, Math.min(8, Number(n) || 1));
      m.weekCycleLabels = m.weekCycleLength > 1 ? Array.from({ length: m.weekCycleLength }, (_, i) => m.weekCycleLabels[i] ?? String.fromCharCode(65 + i)) : [];
    });
  }
  protected setLabel(i: number, v: string): void { this.update((m) => { m.weekCycleLabels[i] = v; }); }

  protected addPeriod(): void {
    this.update((m) => {
      const last = m.periods[m.periods.length - 1];
      const start = last?.end ?? '08:00';
      m.periods.push({ index: m.periods.length, start, end: addMinutes(start, 45), isBreak: false, nameAr: `الفترة ${m.periods.length + 1}`, nameEn: `Period ${m.periods.length + 1}` });
    });
  }
  protected removePeriod(i: number): void {
    this.update((m) => { m.periods.splice(i, 1); m.periods.forEach((p, idx) => (p.index = idx)); });
  }
  protected setPeriod<K extends keyof PeriodDto>(i: number, key: K, v: PeriodDto[K]): void { this.update((m) => { m.periods[i][key] = v; }); }

  protected addShift(): void {
    this.update((m) => m.shifts.push({ id: crypto.randomUUID(), code: `SHIFT${m.shifts.length + 1}`, nameAr: '', nameEn: '', firstSlot: 0, lastSlot: Math.max(0, m.periods.length - 1) }));
  }
  protected removeShift(i: number): void { this.update((m) => { m.shifts.splice(i, 1); }); }
  protected setShift<K extends keyof ShiftDto>(i: number, key: K, v: ShiftDto[K]): void { this.update((m) => { m.shifts[i][key] = v; }); }

  protected isDisabled(day: number, slot: number): boolean {
    return this.model()!.dayOverrides.some((o) => o.dayOfWeek === day && o.slotIndex === slot && o.disabled);
  }
  protected toggleOverride(day: number, slot: number): void {
    this.update((m) => {
      const i = m.dayOverrides.findIndex((o) => o.dayOfWeek === day && o.slotIndex === slot);
      if (i >= 0) m.dayOverrides.splice(i, 1);
      else m.dayOverrides.push({ dayOfWeek: day, slotIndex: slot, disabled: true, start: null, end: null } as DayOverrideDto);
    });
  }

  async save(): Promise<void> {
    this.busy.set(true);
    this.errors.set({});
    try {
      const saved = await firstValueFrom(this.api.put<TimeStructureDto>('/config/time-structure', this.model()));
      this.model.set(saved);
      this.dirty.set(false);
      await this.config.load(true);
      this.toast.success('common.saved');
    } catch (e) {
      if (e instanceof ApiError && e.problem.errors) this.errors.set(Object.fromEntries(Object.entries(e.problem.errors).map(([k, v]) => [k, v[0]?.message ?? ''])));
      this.toast.error(e instanceof ApiError ? e.message : String(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected reset(): void { this.dirty.set(false); this.model.set(structuredClone(this.config.time()!)); }
}

function addMinutes(hhmm: string, minutes: number): string {
  const [h, m] = hhmm.split(':').map(Number);
  const total = Math.min(23 * 60 + 59, h * 60 + m + minutes);
  return `${String(Math.floor(total / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`;
}
