import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError, saveBlob } from '../../core/api/api';
import type { MyDayDto, MyItemDto, MyTimetableDto } from '../../core/api/models';
import { ConfigStore } from '../../core/config/config.store';
import { LanguageService } from '../../core/i18n/language.service';
import { ThemeService } from '../../core/theme/theme.service';
import { ToastService } from '../../core/ui/toast.service';
import { DayNamePipe, LocalDatePipe } from '../../shared/pipes/pipes';
import { deriveColors } from '../../shared/ui/color';
import { Icon } from '../../shared/ui/icon';

/**
 * Self-service week for an instructor or a student group: day tabs on phones, all days side by side on wide screens,
 * per-date substitutions / cancellations, "now" marker and personal PDF export.
 */
@Component({
  selector: 'app-my-timetable-page',
  imports: [TranslocoDirective, Icon, DayNamePipe, LocalDatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './my-timetable.page.html',
  styleUrl: './my-timetable.page.scss',
})
export class MyTimetablePage implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly theme = inject(ThemeService);
  protected readonly lang = inject(LanguageService);
  protected readonly config = inject(ConfigStore);

  protected readonly data = signal<MyTimetableDto | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly anchor = signal(isoDate(new Date()));
  protected readonly selected = signal<string | null>(null);
  protected readonly today = isoDate(new Date());
  protected readonly now = signal(new Date());

  protected readonly day = computed<MyDayDto | null>(() => {
    const d = this.data();
    if (!d) return null;
    return d.days.find((x) => x.date === this.selected()) ?? d.days.find((x) => x.date === this.today) ?? d.days[0] ?? null;
  });

  async ngOnInit(): Promise<void> {
    await this.load();
    setInterval(() => this.now.set(new Date()), 60_000);
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    try {
      const d = await firstValueFrom(this.api.get<MyTimetableDto>('/me/timetable', { date: this.anchor() }));
      this.data.set(d);
      this.error.set(null);
      if (!d.days.some((x) => x.date === this.selected())) this.selected.set(d.days.find((x) => x.date === this.today)?.date ?? d.days[0]?.date ?? null);
    } catch (e) {
      this.data.set(null);
      this.error.set(e instanceof ApiError ? e.message : null);
    } finally {
      this.loading.set(false);
    }
  }

  protected async shiftWeek(weeks: number): Promise<void> {
    const d = new Date(this.anchor() + 'T00:00:00');
    d.setDate(d.getDate() + weeks * 7);
    this.anchor.set(isoDate(d));
    this.selected.set(null);
    await this.load();
  }

  protected async thisWeek(): Promise<void> {
    this.anchor.set(this.today);
    this.selected.set(this.today);
    await this.load();
  }

  protected colors(item: MyItemDto) { return deriveColors(item.color, this.theme.effective()); }

  /** True while the item is running right now (today only). */
  protected isNow(day: MyDayDto, item: MyItemDto): boolean {
    if (day.date !== this.today || item.status === 'cancelled') return false;
    const n = this.now();
    const minutes = n.getHours() * 60 + n.getMinutes();
    return toMinutes(item.start) <= minutes && minutes < toMinutes(item.end);
  }

  protected count(day: MyDayDto): number { return day.items.filter((i) => i.status !== 'cancelled').length; }

  protected async export(): Promise<void> {
    try {
      const blob = await firstValueFrom(this.api.download('/me/timetable/export', { format: 'pdf', lang: this.lang.lang() }));
      saveBlob(blob, 'my-timetable.pdf');
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : null);
    }
  }
}

function isoDate(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

function toMinutes(t: string): number {
  const [h, m] = t.split(':').map(Number);
  return h * 60 + (m || 0);
}
