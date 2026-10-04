import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { CompareChangeDto, CompareDto, PlacementRefDto, ScheduleSummaryDto } from '../../core/api/models';
import { ConfigStore } from '../../core/config/config.store';
import { ScheduleContext } from '../../core/schedule/schedule-context';
import { ToastService } from '../../core/ui/toast.service';
import { RefCache } from '../../shared/forms/ref-cache';
import { DayNamePipe, NumPipe, TermPipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';

type Kind = 'all' | 'moved' | 'added' | 'removed';

/** Side-by-side comparison of two schedule versions: scores and every moved / added / removed occurrence. */
@Component({
  selector: 'app-compare-page',
  imports: [TranslocoDirective, Icon, NumPipe, DayNamePipe, TermPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './compare.page.html',
  styleUrl: './compare.page.scss',
})
export class ComparePage implements OnInit {
  protected readonly context = inject(ScheduleContext);
  private readonly config = inject(ConfigStore);
  private readonly refs = inject(RefCache);
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly transloco = inject(TranslocoService);

  protected readonly a = signal<string | null>(null);
  protected readonly b = signal<string | null>(null);
  protected readonly result = signal<CompareDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly kind = signal<Kind>('all');
  protected readonly kinds: Kind[] = ['all', 'moved', 'added', 'removed'];
  protected readonly changes = computed(() => (this.result()?.changes ?? []).filter((c) => this.kind() === 'all' || c.kind === this.kind()));
  private readonly periods = computed(() => [...this.config.periods()].sort((x, y) => x.index - y.index));

  async ngOnInit(): Promise<void> {
    await this.context.ensureLoaded();
    void this.refs.load('/rooms');
    void this.refs.load('/instructors');
    const q = this.route.snapshot.queryParamMap;
    const list = this.context.schedules();
    this.a.set(q.get('a') ?? this.context.published()?.id ?? list[1]?.id ?? null);
    this.b.set(q.get('b') ?? this.context.currentId() ?? list[0]?.id ?? null);
    await this.run();
  }

  protected label(s: ScheduleSummaryDto): string { return `${s.name} — ${this.transloco.translate('schedules.status.' + s.status)}`; }

  protected async pick(side: 'a' | 'b', id: string): Promise<void> {
    (side === 'a' ? this.a : this.b).set(id || null);
    void this.router.navigate([], { queryParams: { a: this.a(), b: this.b() }, replaceUrl: true });
    await this.run();
  }

  protected async swap(): Promise<void> {
    const a = this.a();
    this.a.set(this.b());
    this.b.set(a);
    await this.run();
  }

  private async run(): Promise<void> {
    const a = this.a(), b = this.b();
    if (!a || !b || a === b) { this.result.set(null); return; }
    this.loading.set(true);
    try {
      this.result.set(await firstValueFrom(this.api.get<CompareDto>('/schedules/compare', { a, b })));
    } catch (e) {
      this.result.set(null);
      this.toast.error(e instanceof ApiError ? e.message : null);
    } finally {
      this.loading.set(false);
    }
  }

  protected sessionLabel(c: CompareChangeDto): string {
    const r = this.result();
    return (r?.sessionLabels[c.sessionId] ?? '') + (c.occurrence > 0 ? ` #${c.occurrence + 1}` : '');
  }

  protected time(p: PlacementRefDto): string {
    return this.periods()[p.startSlot]?.start.slice(0, 5) ?? '';
  }

  protected room(p: PlacementRefDto): string {
    return p.roomId ? (this.refs.items('/rooms').find((r) => r.id === p.roomId)?.code ?? '') : '';
  }

  protected instructor(p: PlacementRefDto): string {
    if (!p.instructorId) return '';
    const i = this.refs.items('/instructors').find((x) => x.id === p.instructorId);
    return i ? this.config.name(i) || i.code || '' : '';
  }

  protected delta(a: number, b: number): number { return b - a; }
}
