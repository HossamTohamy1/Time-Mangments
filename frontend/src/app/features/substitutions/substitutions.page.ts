import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { SubstituteCandidateDto, SubstitutionDto, SubstitutionItemDto } from '../../core/api/models';
import { ConfigStore } from '../../core/config/config.store';
import { ToastService } from '../../core/ui/toast.service';
import { RefCache } from '../../shared/forms/ref-cache';
import { DayNamePipe, LocalDatePipe, NumPipe, TermPipe } from '../../shared/pipes/pipes';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon } from '../../shared/ui/icon';

type StatusFilter = 'Open' | 'Resolved' | 'Cancelled' | 'all';

/** Absences → affected dated sessions → engine-ranked substitutes, or cancel the session for that date. */
@Component({
  selector: 'app-substitutions-page',
  imports: [TranslocoDirective, Icon, DayNamePipe, LocalDatePipe, NumPipe, TermPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './substitutions.page.html',
  styleUrl: './substitutions.page.scss',
})
export class SubstitutionsPage implements OnInit {
  protected readonly refs = inject(RefCache);
  private readonly config = inject(ConfigStore);
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  protected readonly filters: StatusFilter[] = ['Open', 'Resolved', 'Cancelled', 'all'];
  protected readonly filter = signal<StatusFilter>('Open');
  protected readonly list = signal<SubstitutionDto[]>([]);
  protected readonly selected = signal<SubstitutionDto | null>(null);
  protected readonly creating = signal(false);
  protected readonly busy = signal(false);
  protected readonly form = signal({ instructorId: '', from: today(), to: today(), reason: '' });
  protected readonly openItem = signal<string | null>(null);
  protected readonly candidates = signal<SubstituteCandidateDto[] | null>(null);

  protected readonly instructors = computed(() => this.refs.items('/instructors'));
  protected readonly byDate = computed(() => {
    const groups = new Map<string, SubstitutionItemDto[]>();
    for (const i of this.selected()?.items ?? []) {
      const list = groups.get(i.date);
      if (list) list.push(i); else groups.set(i.date, [i]);
    }
    return [...groups.entries()];
  });
  private readonly periods = computed(() => [...this.config.periods()].sort((a, b) => a.index - b.index));

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  protected async load(): Promise<void> {
    try {
      const status = this.filter() === 'all' ? undefined : this.filter();
      this.list.set(await firstValueFrom(this.api.get<SubstitutionDto[]>('/substitutions', { status })));
    } catch (e) { this.fail(e); }
  }

  protected async setFilter(f: StatusFilter): Promise<void> {
    this.filter.set(f);
    await this.load();
  }

  protected patchForm(key: 'instructorId' | 'from' | 'to' | 'reason', value: string): void {
    this.form.update((f) => ({ ...f, [key]: value }));
  }

  protected async create(): Promise<void> {
    const f = this.form();
    if (!f.instructorId) return;
    this.busy.set(true);
    try {
      const s = await firstValueFrom(this.api.post<SubstitutionDto>('/substitutions', {
        absentInstructorId: f.instructorId, fromDate: f.from, toDate: f.to < f.from ? f.from : f.to, reason: f.reason || null,
      }));
      this.creating.set(false);
      this.selected.set(s);
      this.filter.set('Open');
      await this.load();
    } catch (e) { this.fail(e); } finally { this.busy.set(false); }
  }

  protected async open(s: SubstitutionDto): Promise<void> {
    this.creating.set(false);
    this.closeCandidates();
    try { this.selected.set(await firstValueFrom(this.api.get<SubstitutionDto>(`/substitutions/${s.id}`))); } catch (e) { this.fail(e); }
  }

  protected key(i: SubstitutionItemDto): string { return `${i.entryId}:${i.date}`; }

  protected time(i: SubstitutionItemDto): string {
    const p = this.periods();
    const start = p[i.startSlot]?.start.slice(0, 5) ?? '';
    const end = p[Math.min(p.length - 1, i.startSlot + i.duration - 1)]?.end.slice(0, 5) ?? '';
    return `${start}–${end}`;
  }

  protected async findSubstitute(i: SubstitutionItemDto): Promise<void> {
    const s = this.selected();
    if (!s) return;
    if (this.openItem() === this.key(i)) { this.closeCandidates(); return; }
    this.openItem.set(this.key(i));
    this.candidates.set(null);
    try {
      this.candidates.set(await firstValueFrom(this.api.get<SubstituteCandidateDto[]>(`/substitutions/${s.id}/candidates`, { entryId: i.entryId, date: i.date })));
    } catch (e) { this.fail(e); this.closeCandidates(); }
  }

  protected closeCandidates(): void {
    this.openItem.set(null);
    this.candidates.set(null);
  }

  protected async assign(i: SubstitutionItemDto, c: SubstituteCandidateDto): Promise<void> {
    await this.act(`/substitutions/${this.selected()!.id}/assign`, { entryId: i.entryId, date: i.date, instructorId: c.instructorId }, 'substitutions.assigned');
  }

  protected async cancelSession(i: SubstitutionItemDto): Promise<void> {
    const ok = await this.confirm.ask({ titleKey: 'substitutions.cancelTitle', messageKey: 'substitutions.cancelMessage', params: { session: i.session }, danger: true });
    if (ok) await this.act(`/substitutions/${this.selected()!.id}/cancel-session`, { entryId: i.entryId, date: i.date }, 'substitutions.cancelled');
  }

  protected async reopen(i: SubstitutionItemDto): Promise<void> {
    await this.act(`/substitutions/${this.selected()!.id}/reopen`, { entryId: i.entryId, date: i.date }, null);
  }

  protected async close(cancel: boolean): Promise<void> {
    const s = this.selected();
    if (!s) return;
    if (cancel && !(await this.confirm.ask({ titleKey: 'substitutions.discardTitle', messageKey: 'substitutions.discardMessage', danger: true }))) return;
    this.busy.set(true);
    try {
      this.selected.set(await firstValueFrom(this.api.post<SubstitutionDto>(`/substitutions/${s.id}/close`, {}, { cancel })));
      await this.load();
    } catch (e) { this.fail(e); } finally { this.busy.set(false); }
  }

  private async act(path: string, body: unknown, successKey: string | null): Promise<void> {
    this.busy.set(true);
    try {
      this.selected.set(await firstValueFrom(this.api.post<SubstitutionDto>(path, body)));
      this.closeCandidates();
      if (successKey) this.toast.success(successKey);
      void this.load();
    } catch (e) { this.fail(e); } finally { this.busy.set(false); }
  }

  private fail(e: unknown): void { this.toast.error(e instanceof ApiError ? e.message : null); }

  protected dayOf(date: string): number { return new Date(date + 'T00:00:00').getDay(); }
}

function today(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}
