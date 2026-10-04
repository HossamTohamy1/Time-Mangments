import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type {
  BoardResourceDto, BoardSessionDto, EntryDto, MutationResultDto, ScheduleBoardDto, SlotOptionDto, ValidationReportDto, ViolationDto,
} from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { ScheduleContext } from '../../core/schedule/schedule-context';
import { ToastService } from '../../core/ui/toast.service';

export type ViewMode = 'group' | 'instructor' | 'room';

/** A column/row cell of the grid. Slot = 0-based position in the institution's period list. */
export interface GridCell { day: number; slot: number; }

/** What is being placed: a session's next occurrence (from the unplaced list) or an existing entry. */
export interface Pick { sessionId: string; entryId?: string | null; }

export interface UnplacedItem { session: BoardSessionDto; missing: number; }

export const cellKey = (day: number, slot: number) => `${day}:${slot}`;

/** Week-cycle masks: 0 = every week, otherwise bit i = week i. */
export const inWeek = (mask: number, week: number) => mask === 0 || (mask & (1 << week)) !== 0;

/**
 * State of the timetable editor for one schedule: board data, client-side view filtering, server-validated
 * edits applied as deltas, valid-slot overlay while placing, conflicts and live updates from other users.
 */
@Injectable()
export class BoardStore {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthStore);
  private readonly config = inject(ConfigStore);
  private readonly context = inject(ScheduleContext);

  readonly board = signal<ScheduleBoardDto | null>(null);
  readonly entries = signal<Map<string, EntryDto>>(new Map());
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly canUndo = signal(false);
  readonly canRedo = signal(false);
  readonly report = signal<ValidationReportDto | null>(null);

  readonly view = signal<ViewMode>('group');
  readonly resourceId = signal<string | null>(null);
  readonly week = signal(0);

  readonly pick = signal<Pick | null>(null);
  readonly slotOptions = signal<Map<string, SlotOptionDto>>(new Map());
  readonly swapFrom = signal<string | null>(null);

  readonly scheduleId = computed(() => this.board()?.schedule.id ?? null);
  readonly editable = computed(() => this.board()?.editable === true);
  readonly sessions = computed(() => new Map((this.board()?.sessions ?? []).map((s) => [s.id, s])));
  readonly groups = computed(() => new Map((this.board()?.groups ?? []).map((g) => [g.id, g])));
  readonly instructors = computed(() => new Map((this.board()?.instructors ?? []).map((i) => [i.id, i])));
  readonly rooms = computed(() => new Map((this.board()?.rooms ?? []).map((r) => [r.id, r])));

  /** Resources available in the current view's selector. */
  readonly resources = computed<BoardResourceDto[]>(() => {
    const b = this.board();
    if (!b) return [];
    return this.view() === 'group' ? b.groups : this.view() === 'instructor' ? b.instructors : b.rooms;
  });

  /** Group + its ancestors + descendants: a class sees its cohort's shared lectures and its sub-groups' labs. */
  private readonly relatedGroups = computed(() => {
    const id = this.resourceId();
    const related = new Set<string>();
    if (!id || this.view() !== 'group') return related;
    const groups = this.groups();
    let cur: string | null | undefined = id;
    while (cur && !related.has(cur)) { related.add(cur); cur = groups.get(cur)?.parentId; }
    const queue = [id];
    while (queue.length) {
      const g = queue.shift()!;
      for (const child of this.board()?.groups ?? []) if (child.parentId === g && !related.has(child.id)) { related.add(child.id); queue.push(child.id); }
    }
    return related;
  });

  private matches(session: BoardSessionDto | undefined, e?: EntryDto): boolean {
    if (!session) return false;
    const id = this.resourceId();
    if (!id) return false;
    switch (this.view()) {
      case 'group': return session.groupIds.some((g) => this.relatedGroups().has(g));
      case 'instructor': return e ? e.instructorId === id : session.fixedInstructorId === id || session.instructorOptions.includes(id);
      case 'room': return e ? e.roomId === id : true;
    }
  }

  /** Entries shown in the grid for the selected resource and week. */
  readonly visibleEntries = computed(() => {
    const out: EntryDto[] = [];
    const sessions = this.sessions();
    for (const e of this.entries().values()) {
      if (inWeek(e.weekMask, this.week()) && this.matches(sessions.get(e.sessionId), e)) out.push(e);
    }
    return out;
  });

  /** Visible entries by start cell. */
  readonly byCell = computed(() => {
    const map = new Map<string, EntryDto[]>();
    for (const e of this.visibleEntries()) {
      const k = cellKey(e.day, e.startSlot);
      const list = map.get(k);
      if (list) list.push(e); else map.set(k, [e]);
    }
    return map;
  });

  /** Occurrences still to place, for sessions relevant to the current selection. */
  readonly unplaced = computed<UnplacedItem[]>(() => {
    const counts = new Map<string, number>();
    for (const e of this.entries().values()) counts.set(e.sessionId, (counts.get(e.sessionId) ?? 0) + 1);
    const out: UnplacedItem[] = [];
    for (const s of this.board()?.sessions ?? []) {
      const missing = s.perWeek - (counts.get(s.id) ?? 0);
      if (missing > 0 && (this.view() === 'room' || this.matches(s))) out.push({ session: s, missing });
    }
    return out;
  });

  readonly unplacedTotal = computed(() => this.unplaced().reduce((n, u) => n + u.missing, 0));

  /** Violations per entry (from the last full validation). */
  readonly violationsByEntry = computed(() => {
    const map = new Map<string, ViolationDto[]>();
    for (const v of this.report()?.violations ?? []) {
      for (const ref of v.entities) {
        if (ref.kind !== 'Entry') continue;
        const list = map.get(ref.id);
        if (list) list.push(v); else map.set(ref.id, [v]);
      }
    }
    return map;
  });

  readonly hardCount = computed(() => this.report()?.hardCount ?? this.board()?.schedule.hardViolations ?? 0);

  constructor() {
    inject(RealtimeService).scheduleChanged$.pipe(takeUntilDestroyed(inject(DestroyRef))).subscribe((e) => {
      if (e.scheduleId !== this.scheduleId()) return;
      if (e.kind === 'revalidated') { void this.refreshConflicts(); return; }
      if (e.userId && e.userId === this.auth.me()?.profile.id) return;
      if (e.upserted || e.removed) {
        this.applyDelta(e.upserted ?? [], e.removed ?? []);
        if (e.userName) this.toast.info('timetable.editor.remoteChange', { name: e.userName });
        this.scheduleConflicts();
      } else if (e.kind === 'published' || e.kind === 'deleted') {
        void this.load(this.scheduleId()!);
      }
    });
  }

  async load(scheduleId: string): Promise<void> {
    this.loading.set(true);
    try {
      const b = await firstValueFrom(this.api.get<ScheduleBoardDto>(`/schedules/${scheduleId}/board`));
      this.board.set(b);
      this.entries.set(new Map(b.entries.map((e) => [e.id, e])));
      this.canUndo.set(b.canUndo);
      this.canRedo.set(b.canRedo);
      this.report.set(null);
      const list = this.resources();
      if (!this.resourceId() || !list.some((r) => r.id === this.resourceId())) this.resourceId.set(this.defaultResource());
      void this.refreshConflicts();
    } finally {
      this.loading.set(false);
    }
  }

  /** Prefer a top-level group with sessions; otherwise the first resource. */
  private defaultResource(): string | null {
    const b = this.board();
    if (!b) return null;
    if (this.view() === 'group') {
      const used = new Set(b.sessions.flatMap((s) => s.groupIds));
      return (b.groups.find((g) => used.has(g.id)) ?? b.groups[0])?.id ?? null;
    }
    return this.resources()[0]?.id ?? null;
  }

  setView(v: ViewMode, resourceId?: string | null): void {
    this.view.set(v);
    this.resourceId.set(resourceId ?? null);
    if (!resourceId) this.resourceId.set(this.defaultResource());
  }

  private conflictTimer: ReturnType<typeof setTimeout> | null = null;

  /** Debounced full validation (conflict highlighting) after edits. */
  scheduleConflicts(): void {
    if (this.conflictTimer) clearTimeout(this.conflictTimer);
    this.conflictTimer = setTimeout(() => void this.refreshConflicts(), 400);
  }

  async refreshConflicts(): Promise<void> {
    const id = this.scheduleId();
    if (!id) return;
    try {
      const r = await firstValueFrom(this.api.get<ValidationReportDto>(`/schedules/${id}/conflicts`));
      if (id !== this.scheduleId()) return;
      this.report.set(r);
      this.context.patch(id, { hardViolations: r.hardCount, softScore: r.softPenalty });
    } catch { /* conflicts are best-effort; the grid stays usable */ }
  }

  private applyDelta(upserted: EntryDto[], removed: string[]): void {
    this.entries.update((m) => {
      const next = new Map(m);
      for (const id of removed) next.delete(id);
      for (const e of upserted) next.set(e.id, e);
      return next;
    });
  }

  // ---- placing ------------------------------------------------------------------------------------

  async startPick(p: Pick): Promise<void> {
    this.pick.set(p);
    this.slotOptions.set(new Map());
    const id = this.scheduleId();
    if (!id) return;
    try {
      const list = await firstValueFrom(this.api.get<SlotOptionDto[]>(`/sessions/${p.sessionId}/valid-slots`, { scheduleId: id, entryId: p.entryId ?? undefined }));
      if (this.pick() !== p) return;
      this.slotOptions.set(new Map(list.map((o) => [cellKey(o.day, o.startSlot), o])));
    } catch (e) {
      this.fail(e);
    }
  }

  cancelPick(): void {
    this.pick.set(null);
    this.slotOptions.set(new Map());
  }

  option(day: number, slot: number): SlotOptionDto | undefined {
    return this.slotOptions().get(cellKey(day, slot));
  }

  /** Places the picked session/entry on a cell (server re-validates; hard violations are rejected). */
  async placeAt(cell: GridCell): Promise<boolean> {
    const p = this.pick();
    if (!p) return false;
    const opt = this.option(cell.day, cell.slot);
    if (opt?.status === 'invalid') {
      this.toast.warning(opt.reasons.map((r) => r.message).join(' • ') || null, 'timetable.editor.invalidSlot');
      return false;
    }
    const entry = p.entryId ? this.entries().get(p.entryId) : undefined;
    if (entry && entry.day === cell.day && entry.startSlot === cell.slot) { this.cancelPick(); return false; }
    const ok = entry
      ? await this.mutate(`/schedules/${this.scheduleId()}/entries/${entry.id}/move`, 'put', {
          day: cell.day, startSlot: cell.slot, roomId: opt?.roomId ?? null, instructorId: opt?.instructorId ?? null, rowVersion: entry.rowVersion,
        })
      : await this.mutate(`/schedules/${this.scheduleId()}/entries`, 'post', {
          sessionId: p.sessionId, day: cell.day, startSlot: cell.slot, roomId: opt?.roomId ?? null, instructorId: opt?.instructorId ?? null,
          weekMask: null,
        });
    this.cancelPick();
    return ok;
  }

  unplace(entry: EntryDto): Promise<boolean> {
    return this.mutate(`/schedules/${this.scheduleId()}/entries/${entry.id}?rowVersion=${encodeURIComponent(entry.rowVersion)}`, 'delete');
  }

  togglePin(entry: EntryDto): Promise<boolean> {
    return this.mutate(`/schedules/${this.scheduleId()}/entries/${entry.id}/pin`, 'put', { pinned: !entry.pinned });
  }

  async swapWith(target: EntryDto): Promise<boolean> {
    const from = this.swapFrom();
    this.swapFrom.set(null);
    if (!from || from === target.id) return false;
    return this.mutate(`/schedules/${this.scheduleId()}/entries/swap`, 'post', { firstEntryId: from, secondEntryId: target.id });
  }

  undo(): Promise<boolean> { return this.mutate(`/schedules/${this.scheduleId()}/undo`, 'post', {}); }
  redo(): Promise<boolean> { return this.mutate(`/schedules/${this.scheduleId()}/redo`, 'post', {}); }

  async autoPlace(sessionIds?: string[]): Promise<MutationResultDto | null> {
    let result: MutationResultDto | null = null;
    await this.mutate(`/schedules/${this.scheduleId()}/entries/auto-place`, 'post', { sessionIds: sessionIds ?? null }, (r) => (result = r));
    return result;
  }

  private async mutate(path: string, method: 'post' | 'put' | 'delete', body?: unknown, onResult?: (r: MutationResultDto) => void): Promise<boolean> {
    if (!this.scheduleId() || this.busy()) return false;
    this.busy.set(true);
    try {
      const req = method === 'post' ? this.api.post<MutationResultDto>(path, body) : method === 'put' ? this.api.put<MutationResultDto>(path, body) : this.api.delete<MutationResultDto>(path);
      const r = await firstValueFrom(req);
      this.applyDelta(r.upserted, r.removed);
      this.canUndo.set(r.canUndo);
      this.canRedo.set(r.canRedo);
      if (r.warnings.length) this.toast.warning(r.warnings.map((w) => w.message).join(' • '), 'timetable.editor.softWarning');
      onResult?.(r);
      this.scheduleConflicts();
      return true;
    } catch (e) {
      this.fail(e);
      if (e instanceof ApiError && e.code === 'CONCURRENCY_CONFLICT') void this.load(this.scheduleId()!);
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  private fail(e: unknown): void {
    if (!(e instanceof ApiError)) { this.toast.error(null); return; }
    if (e.status === 0 || e.status >= 500) return; // already reported by the error interceptor
    const reasons = Array.isArray(e.problem.details) ? (e.problem.details as ViolationDto[]).map((v) => v.message).filter(Boolean) : [];
    this.toast.error([e.message, ...reasons].join(' • '));
  }

  /** Display helpers. */
  label(r: { nameAr?: string | null; nameEn?: string | null; code?: string } | undefined): string {
    if (!r) return '';
    return this.config.name(r) || r.code || '';
  }
}
