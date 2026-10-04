import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api } from '../../core/api/api';
import type { DashboardDto } from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { LocalDatePipe, LocalNamePipe, NumPipe, DayNamePipe, TermPipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';

/** Operations dashboard: coverage, conflicts, load and utilisation of the current timetable, plus recent activity. */
@Component({
  selector: 'app-dashboard-page',
  imports: [TranslocoDirective, RouterLink, Icon, LocalNamePipe, TermPipe, NumPipe, DayNamePipe, LocalDatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dashboard.page.html',
  styleUrl: './dashboard.page.scss',
})
export class DashboardPage implements OnInit {
  protected readonly auth = inject(AuthStore);
  protected readonly config = inject(ConfigStore);
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly transloco = inject(TranslocoService);

  protected readonly data = signal<DashboardDto | null>(null);
  protected readonly loading = signal(true);
  protected readonly maxLoad = computed(() => Math.max(1, ...(this.data()?.instructorLoad ?? []).map((l) => Math.max(l.periods, l.maxPeriods ?? 0))));
  protected readonly maxDay = computed(() => Math.max(1, ...(this.data()?.perDay ?? []).map((d) => d.count)));

  async ngOnInit(): Promise<void> {
    if (!this.auth.has('dashboard.view')) {
      // Self-service accounts land on their own timetable.
      const p = this.auth.me()?.profile;
      if (this.auth.has('timetable.view.own') && (p?.instructorId || p?.studentGroupId)) void this.router.navigateByUrl('/my-timetable', { replaceUrl: true });
      this.loading.set(false);
      return;
    }
    try { this.data.set(await firstValueFrom(this.api.get<DashboardDto>('/dashboard'))); }
    catch { this.data.set(null); }
    finally { this.loading.set(false); }
  }

  protected pct(used: number, capacity: number): number { return capacity <= 0 ? 0 : Math.min(100, Math.round((100 * used) / capacity)); }

  protected constraintName(code: string): string {
    const key = `constraints.${code}.name`;
    const v = this.transloco.translate(key);
    return v === key ? code : v;
  }
}
