import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
import type { ScheduleSummaryDto, ViolationDto } from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { ScheduleContext } from '../../core/schedule/schedule-context';
import { DayNamePipe, NumPipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';
import { BoardStore } from './board.store';

type Severity = 'all' | 'Hard' | 'Soft';

interface Chip { kind: string; icon: string; label: string; }
interface ConflictGroup { code: string; name: string; severity: string; penalty: number; items: ViolationDto[]; }

/** All findings of a schedule grouped by constraint, with links into the editor to fix them. */
@Component({
  selector: 'app-conflicts-page',
  imports: [TranslocoDirective, RouterLink, Icon, DayNamePipe, NumPipe],
  providers: [BoardStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './conflicts.page.html',
  styleUrl: './conflicts.page.scss',
})
export class ConflictsPage {
  protected readonly store = inject(BoardStore);
  protected readonly context = inject(ScheduleContext);
  private readonly config = inject(ConfigStore);
  private readonly auth = inject(AuthStore);
  private readonly transloco = inject(TranslocoService);

  protected readonly severity = signal<Severity>('Hard');
  protected readonly search = signal('');
  protected readonly severities: Severity[] = ['Hard', 'Soft', 'all'];

  protected readonly canEdit = computed(() => this.store.editable() && this.auth.has('timetable.edit'));
  private readonly periods = computed(() => [...this.config.periods()].sort((a, b) => a.index - b.index));

  protected readonly groups = computed<ConflictGroup[]>(() => {
    const q = this.search().trim().toLowerCase();
    const sev = this.severity();
    const map = new Map<string, ConflictGroup>();
    for (const v of this.store.report()?.violations ?? []) {
      if (sev !== 'all' && v.severity !== sev) continue;
      if (v.severity !== 'Hard' && v.penalty <= 0) continue;
      if (q && !v.message.toLowerCase().includes(q) && !this.chips(v).some((c) => c.label.toLowerCase().includes(q))) continue;
      let g = map.get(v.constraintCode);
      if (!g) map.set(v.constraintCode, (g = { code: v.constraintCode, name: this.constraintName(v.constraintCode), severity: v.severity, penalty: 0, items: [] }));
      g.items.push(v);
      g.penalty += v.penalty;
    }
    return [...map.values()].sort((a, b) => (a.severity === b.severity ? b.items.length - a.items.length : a.severity === 'Hard' ? -1 : 1));
  });

  protected readonly softCount = computed(() => (this.store.report()?.violations ?? []).filter((v) => v.severity !== 'Hard' && v.penalty > 0).length);

  constructor() {
    void this.context.ensureLoaded();
    effect(() => {
      const id = this.context.currentId();
      if (id) untracked(() => void this.store.load(id));
    });
  }

  protected constraintName(code: string): string {
    const key = `constraints.${code}.name`;
    const name = this.transloco.translate(key);
    return name && name !== key ? name : code;
  }

  protected time(v: ViolationDto): string {
    if (v.day === null || v.day === undefined) return '';
    const p = v.slot !== null && v.slot !== undefined ? this.periods()[v.slot] : undefined;
    return p ? p.start.slice(0, 5) : '';
  }

  /** Readable chips for the entities a finding refers to. */
  protected chips(v: ViolationDto): Chip[] {
    const out: Chip[] = [];
    const seen = new Set<string>();
    for (const ref of v.entities) {
      const key = ref.kind + ref.id;
      if (seen.has(key)) continue;
      seen.add(key);
      switch (ref.kind) {
        case 'Session': {
          const s = this.store.sessions().get(ref.id);
          if (s) out.push({ kind: 'session', icon: 'book', label: s.courseCode });
          break;
        }
        case 'Instructor': out.push({ kind: 'instructor', icon: 'user', label: this.store.label(this.store.instructors().get(ref.id)) }); break;
        case 'Room': out.push({ kind: 'room', icon: 'door', label: this.store.label(this.store.rooms().get(ref.id)) }); break;
        case 'Group': out.push({ kind: 'group', icon: 'cap', label: this.store.groups().get(ref.id)?.code ?? '' }); break;
      }
    }
    return out.filter((c) => c.label);
  }

  protected entryId(v: ViolationDto): string | null {
    return v.entities.find((e) => e.kind === 'Entry')?.id ?? null;
  }

  protected selectSchedule(id: string): void { this.context.select(id); }

  protected scheduleLabel(s: ScheduleSummaryDto): string {
    return `${s.name} — ${this.transloco.translate('schedules.status.' + s.status)}`;
  }
}
