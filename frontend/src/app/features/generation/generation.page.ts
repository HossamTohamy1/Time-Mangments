import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { ConstraintSeverity, GenerationJobDto, ReadinessDto, ScheduleSummaryDto } from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { ScheduleContext } from '../../core/schedule/schedule-context';
import { ToastService } from '../../core/ui/toast.service';
import { RefCache } from '../../shared/forms/ref-cache';
import { LocalDatePipe, NumPipe, TermPipe } from '../../shared/pipes/pipes';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon } from '../../shared/ui/icon';
import { GenerationHub, GenerationProgressEvent } from './generation-hub';

type Step = 'scope' | 'engine' | 'check' | 'run';
type Engine = 'auto' | 'cpsat' | 'heuristic';
interface Override { severity: ConstraintSeverity; weight: number; }

/** Generation wizard: scope → engine & limits → readiness → live run with results and next steps. */
@Component({
  selector: 'app-generation-page',
  imports: [TranslocoDirective, Icon, NumPipe, LocalDatePipe, TermPipe],
  providers: [GenerationHub],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './generation.page.html',
  styleUrl: './generation.page.scss',
})
export class GenerationPage implements OnInit {
  protected readonly context = inject(ScheduleContext);
  protected readonly config = inject(ConfigStore);
  protected readonly refs = inject(RefCache);
  protected readonly auth = inject(AuthStore);
  private readonly api = inject(Api);
  private readonly hub = inject(GenerationHub);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly router = inject(Router);
  private readonly transloco = inject(TranslocoService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly steps: Step[] = ['scope', 'engine', 'check', 'run'];
  protected readonly step = signal<Step>('scope');

  protected readonly termId = signal<string | null>(null);
  protected readonly baseScheduleId = signal<string | null>(null);
  protected readonly mode = signal<'fresh' | 'complete'>('fresh');
  protected readonly name = signal('');
  protected readonly engine = signal<Engine>('auto');
  protected readonly timeLimit = signal(60);
  protected readonly useHints = signal(true);
  protected readonly overrides = signal<Map<string, Override>>(new Map());
  protected readonly showAdvanced = signal(false);

  protected readonly readiness = signal<ReadinessDto | null>(null);
  protected readonly checking = signal(false);
  protected readonly job = signal<GenerationJobDto | null>(null);
  protected readonly live = signal<GenerationProgressEvent | null>(null);
  protected readonly jobs = signal<GenerationJobDto[]>([]);
  protected readonly elapsed = signal(0);
  protected readonly starting = signal(false);

  protected readonly engines: { id: Engine; icon: string }[] = [{ id: 'auto', icon: 'spark' }, { id: 'cpsat', icon: 'target' }, { id: 'heuristic', icon: 'wand' }];
  protected readonly terms = computed(() => this.refs.items('/terms'));
  protected readonly baseOptions = computed(() => this.context.schedules().filter((s) => s.termId === this.termId()));
  protected readonly tunable = computed(() => (this.config.config()?.constraints ?? []).filter((c) => !c.isCore));
  protected readonly running = computed(() => ['Queued', 'Running'].includes(this.job()?.status ?? ''));
  protected readonly finished = computed(() => !!this.job() && !this.running());
  protected readonly percent = computed(() => {
    const j = this.job();
    if (!j) return 0;
    if (!this.running()) return 100;
    return Math.max(j.progress, this.live()?.jobId === j.id ? this.live()!.percent : 0);
  });
  protected readonly errors = computed(() => (this.readiness()?.issues ?? []).filter((i) => i.severity === 'error'));
  protected readonly warnings = computed(() => (this.readiness()?.issues ?? []).filter((i) => i.severity === 'warning'));

  private timer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    this.hub.progress$.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e.jobId !== this.job()?.id) return;
      this.live.set(e);
      if (e.final) void this.refreshJob();
    });
    this.destroyRef.onDestroy(() => this.stopTimer());
  }

  async ngOnInit(): Promise<void> {
    void this.hub.connect();
    await Promise.all([this.context.ensureLoaded(), this.refs.load('/terms'), this.loadJobs()]);
    const current = this.terms().find((t) => t['isCurrent']) ?? this.terms()[0];
    this.termId.set(this.context.current()?.termId ?? current?.id ?? null);
    const active = this.jobs().find((j) => j.status === 'Queued' || j.status === 'Running');
    if (active) { this.job.set(active); this.step.set('run'); this.startTimer(); }
  }

  protected stepIndex(s: Step): number { return this.steps.indexOf(s); }

  protected go(s: Step): void {
    if (this.running() && s !== 'run') return;
    this.step.set(s);
    if (s === 'check') void this.check();
  }

  protected setTerm(id: string): void {
    this.termId.set(id || null);
    this.baseScheduleId.set(null);
    this.readiness.set(null);
  }

  protected scheduleLabel(s: ScheduleSummaryDto): string {
    return `${s.name} — ${this.transloco.translate('schedules.status.' + s.status)}`;
  }

  protected overrideOf(code: string, field: 'severity' | 'weight'): string | number {
    const o = this.overrides().get(code);
    const c = this.tunable().find((x) => x.code === code)!;
    return field === 'severity' ? (o?.severity ?? c.severity) : (o?.weight ?? c.weight);
  }

  protected setOverride(code: string, field: 'severity' | 'weight', value: string): void {
    const c = this.tunable().find((x) => x.code === code)!;
    this.overrides.update((m) => {
      const n = new Map(m);
      const cur = n.get(code) ?? { severity: c.severity, weight: c.weight };
      const next = field === 'severity' ? { ...cur, severity: value as ConstraintSeverity } : { ...cur, weight: Math.max(1, Math.min(100, Number(value) || 1)) };
      if (next.severity === c.severity && next.weight === c.weight) n.delete(code); else n.set(code, next);
      return n;
    });
  }

  protected constraintName(code: string): string { return this.transloco.translate(`constraints.${code}.name`); }

  protected async check(): Promise<void> {
    const term = this.termId();
    if (!term) return;
    this.checking.set(true);
    try {
      this.readiness.set(await firstValueFrom(this.api.get<ReadinessDto>('/generation/readiness', { termId: term })));
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : null);
    } finally {
      this.checking.set(false);
    }
  }

  protected async start(): Promise<void> {
    const term = this.termId();
    if (!term) return;
    if (this.errors().length > 0) {
      const ok = await this.confirm.ask({ titleKey: 'generation.check.proceedTitle', messageKey: 'generation.check.proceedMessage', params: { count: this.errors().length } });
      if (!ok) return;
    }
    this.starting.set(true);
    try {
      const constraintOverrides = [...this.overrides().entries()].map(([code, o]) => {
        const c = this.tunable().find((x) => x.code === code)!;
        return { code, severity: o.severity, weight: o.weight, parametersJson: c.parametersJson };
      });
      const job = await firstValueFrom(this.api.post<GenerationJobDto>('/generation/jobs', {
        termId: term, baseScheduleId: this.baseScheduleId(), name: this.name().trim() || null, engine: this.engine(), mode: this.mode(),
        timeLimitSeconds: this.timeLimit(), useHints: this.useHints(), constraintOverrides,
      }));
      this.job.set(job);
      this.live.set(null);
      this.step.set('run');
      this.startTimer();
      void this.loadJobs();
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : null);
    } finally {
      this.starting.set(false);
    }
  }

  protected async cancel(): Promise<void> {
    const j = this.job();
    if (!j) return;
    try { this.job.set(await firstValueFrom(this.api.post<GenerationJobDto>(`/generation/jobs/${j.id}/cancel`))); }
    catch (e) { this.toast.error(e instanceof ApiError ? e.message : null); }
  }

  private startTimer(): void {
    this.stopTimer();
    const tick = async () => {
      const j = this.job();
      if (!j) return;
      const started = j.startedAt ? Date.parse(j.startedAt) : Date.now();
      this.elapsed.set(Math.max(0, Math.round((Date.now() - started) / 1000)));
      if (this.running()) await this.refreshJob();
      else this.stopTimer();
    };
    this.timer = setInterval(() => void tick(), 1500);
  }

  private stopTimer(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  private async refreshJob(): Promise<void> {
    const j = this.job();
    if (!j) return;
    try {
      const fresh = await firstValueFrom(this.api.get<GenerationJobDto>(`/generation/jobs/${j.id}`));
      this.job.set(fresh);
      if (!['Queued', 'Running'].includes(fresh.status)) {
        this.stopTimer();
        await this.context.refresh();
        void this.loadJobs();
      }
    } catch { /* keep the last known state */ }
  }

  private async loadJobs(): Promise<void> {
    try { this.jobs.set(await firstValueFrom(this.api.get<GenerationJobDto[]>('/generation/jobs'))); } catch { /* optional */ }
  }

  protected openResult(): void {
    const id = this.job()?.resultScheduleId;
    if (!id) return;
    this.context.select(id);
    void this.router.navigateByUrl('/timetable');
  }

  protected compareResult(): void {
    const id = this.job()?.resultScheduleId;
    const other = this.context.published()?.id ?? this.job()?.baseScheduleId;
    if (!id) return;
    void this.router.navigate(['/schedules/compare'], { queryParams: { a: other ?? null, b: id } });
  }

  protected newRun(): void {
    this.job.set(null);
    this.live.set(null);
    this.step.set('scope');
  }

  protected phaseText(): string {
    const l = this.live();
    if (l && l.jobId === this.job()?.id) return l.message;
    const phase = this.job()?.phase;
    return phase ? this.transloco.translate(`generation.phases.${phase}`) : this.transloco.translate('generation.run.queued');
  }

  protected durationLabel(value: number): string {
    const seconds = Math.round(value);
    const m = Math.floor(seconds / 60);
    const s = seconds % 60;
    return `${m}:${String(s).padStart(2, '0')}`;
  }
}
