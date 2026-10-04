import { CdkDrag, CdkDragDrop, CdkDragPlaceholder, CdkDragStart, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { CdkMenu, CdkMenuItem, CdkMenuItemCheckbox, CdkMenuTrigger } from '@angular/cdk/menu';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { ChangeDetectionStrategy, Component, ElementRef, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError, PagedResult } from '../../core/api/api';
import type { EntryDto, ScheduleSummaryDto, TermDto, ValidationReportDto } from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { LanguageService } from '../../core/i18n/language.service';
import { ScheduleContext } from '../../core/schedule/schedule-context';
import { ToastService } from '../../core/ui/toast.service';
import { HasPermissionDirective } from '../../shared/directives/has-permission.directive';
import { DayNamePipe, LocalNamePipe, NumPipe, TermPipe } from '../../shared/pipes/pipes';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon } from '../../shared/ui/icon';
import { BoardStore, GridCell, UnplacedItem, ViewMode, cellKey } from './board.store';
import { SessionCard } from './session-card';

interface GridRow { slot: number; start: string; end: string; isBreak: boolean; nameAr?: string | null; nameEn?: string | null; minutes: number; }
interface DragData { sessionId: string; entryId?: string | null; }

/**
 * Timetable editor: days × periods grid per student group / instructor / room, drag-and-drop or keyboard placement
 * with live valid-slot highlighting (validated by the same engine as generation), unplaced panel, pin / swap /
 * alternative slot, undo / redo, validation and publishing.
 */
@Component({
  selector: 'app-timetable-page',
  imports: [
    TranslocoDirective, RouterLink, Icon, SessionCard, CdkDropListGroup, CdkDropList, CdkDrag, CdkDragPlaceholder,
    CdkMenu, CdkMenuItem, CdkMenuItemCheckbox, CdkMenuTrigger, ScrollingModule, DayNamePipe, TermPipe, LocalNamePipe, NumPipe, HasPermissionDirective,
  ],
  providers: [BoardStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './timetable.page.html',
  styleUrl: './timetable.page.scss',
  host: { '(document:keydown.escape)': 'cancel()' },
})
export class TimetablePage implements OnInit {
  protected readonly store = inject(BoardStore);
  protected readonly context = inject(ScheduleContext);
  protected readonly config = inject(ConfigStore);
  protected readonly lang = inject(LanguageService);
  protected readonly auth = inject(AuthStore);
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly transloco = inject(TranslocoService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);

  protected readonly zoom = signal(1);
  protected readonly search = signal('');
  protected readonly typeChip = signal<string | null>(null);
  protected readonly hiddenTypes = signal<Set<string>>(new Set());
  protected readonly hover = signal<string | null>(null);
  protected readonly dragging = signal(false);
  protected readonly highlight = signal<string | null>(null);

  protected readonly days = this.config.workingDays;
  protected readonly weekLabels = this.config.weekLabels;
  protected readonly views: ViewMode[] = ['group', 'instructor', 'room'];
  protected readonly todayDow = new Date().getDay();

  protected readonly rows = computed<GridRow[]>(() =>
    [...this.config.periods()].sort((a, b) => a.index - b.index).map((p, i) => ({
      slot: i, start: p.start.slice(0, 5), end: p.end.slice(0, 5), isBreak: p.isBreak, nameAr: p.nameAr, nameEn: p.nameEn,
      minutes: toMinutes(p.end) - toMinutes(p.start),
    })));

  private readonly slotByIndex = computed(() => new Map([...this.config.periods()].sort((a, b) => a.index - b.index).map((p, i) => [p.index, i])));

  /** Cells switched off by day overrides (e.g. a short Thursday). */
  protected readonly disabledCells = computed(() => {
    const set = new Set<string>();
    for (const o of this.config.time()?.dayOverrides ?? []) {
      const slot = this.slotByIndex().get(o.slotIndex);
      if (o.disabled && slot !== undefined) set.add(cellKey(o.dayOfWeek, slot));
    }
    return set;
  });

  /** Days with parallel sessions (e.g. sub-group labs) get proportionally wider columns. */
  private readonly dayFactors = computed(() => {
    const max = new Map<number, number>();
    for (const [key, list] of this.store.byCell()) {
      const day = Number(key.split(':')[0]);
      max.set(day, Math.max(max.get(day) ?? 1, list.length));
    }
    return this.days().map((d) => 1 + (Math.min(max.get(d) ?? 1, 4) - 1) * 0.6);
  });

  protected readonly gridColumns = computed(() =>
    'var(--time-w) ' + this.dayFactors().map((f) => `minmax(calc(var(--col-min) * ${f}), ${f}fr)`).join(' '));

  protected readonly columnUnits = computed(() => this.dayFactors().reduce((a, b) => a + b, 0));

  protected readonly sessionTypes = computed(() => {
    const used = new Set((this.store.board()?.sessions ?? []).map((s) => s.sessionTypeId));
    return this.config.lookups('session-types', false).filter((t) => used.has(t.id));
  });

  protected readonly unplacedChips = computed(() => {
    const counts = new Map<string, number>();
    for (const u of this.store.unplaced()) counts.set(u.session.sessionTypeId, (counts.get(u.session.sessionTypeId) ?? 0) + u.missing);
    return this.sessionTypes().filter((t) => counts.has(t.id)).map((t) => ({ type: t, count: counts.get(t.id)! }));
  });

  protected readonly filteredUnplaced = computed(() => {
    const q = this.search().trim().toLowerCase();
    const chip = this.typeChip();
    return this.store.unplaced().filter((u) => {
      if (chip && u.session.sessionTypeId !== chip) return false;
      if (!q) return true;
      const s = u.session;
      return [s.courseCode, s.nameAr, s.nameEn, ...s.groupIds.map((g) => this.store.label(this.store.groups().get(g)))]
        .some((x) => x?.toLowerCase().includes(q));
    });
  });

  protected readonly pickedSession = computed(() => {
    const p = this.store.pick();
    return p ? this.store.sessions().get(p.sessionId) ?? null : null;
  });

  protected readonly selectedResource = computed(() => this.store.resources().find((r) => r.id === this.store.resourceId()) ?? null);
  protected readonly canPublish = computed(() => this.auth.has('schedule.publish'));
  protected readonly canEdit = computed(() => this.store.editable() && this.auth.has('timetable.edit'));

  constructor() {
    effect(() => {
      const id = this.context.currentId();
      if (id) untracked(() => void this.store.load(id).then(() => this.applyDeepLink()));
    });
  }

  async ngOnInit(): Promise<void> {
    const q = this.route.snapshot.queryParamMap;
    const scheduleId = q.get('scheduleId');
    const view = q.get('view') as ViewMode | null;
    if (view && this.views.includes(view)) this.store.setView(view, q.get('id'));
    await this.context.ensureLoaded();
    if (scheduleId && scheduleId !== this.context.currentId()) this.context.select(scheduleId);
  }

  /** ?entry=… highlights (and shows) an entry, ?pick=… opens "find alternative slot" for it. */
  private applyDeepLink(): void {
    const q = this.route.snapshot.queryParamMap;
    const entryId = q.get('pick') ?? q.get('entry');
    if (!entryId) return;
    const e = this.store.entries().get(entryId);
    if (!e) return;
    const session = this.store.sessions().get(e.sessionId);
    if (!q.get('view') && session?.groupIds[0]) this.store.setView('group', session.groupIds[0]);
    this.store.week.set(this.firstWeek(e.weekMask));
    this.highlight.set(e.id);
    setTimeout(() => this.host.nativeElement.querySelector(`[data-entry="${e.id}"]`)?.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' }), 50);
    setTimeout(() => this.highlight.set(null), 3500);
    if (q.get('pick') && this.canEdit()) void this.store.startPick({ sessionId: e.sessionId, entryId: e.id });
    void this.router.navigate([], { queryParams: { entry: null, pick: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  private firstWeek(mask: number): number {
    if (mask === 0) return this.store.week();
    for (let i = 0; i < 31; i++) if (mask & (1 << i)) return i;
    return 0;
  }

  // ---- view / toolbar ------------------------------------------------------------------------------

  protected setView(v: ViewMode): void { this.store.setView(v); }

  protected selectResource(id: string): void { this.store.resourceId.set(id || null); }

  protected selectSchedule(id: string): void { this.context.select(id); }

  protected zoomBy(delta: number): void { this.zoom.update((z) => Math.min(1.5, Math.max(0.7, Math.round((z + delta) * 10) / 10))); }

  protected toggleType(id: string): void {
    this.hiddenTypes.update((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  }

  protected resourceLabel(r: { code: string; nameAr?: string | null; nameEn?: string | null }): string {
    const name = this.config.name(r);
    return name && name !== r.code ? `${r.code} · ${name}` : r.code;
  }

  protected scheduleLabel(s: ScheduleSummaryDto): string {
    return `${s.name} — ${this.transloco.translate('schedules.status.' + s.status)}`;
  }

  // ---- card helpers ---------------------------------------------------------------------------------

  protected entriesAt(day: number, slot: number): EntryDto[] {
    return this.store.byCell().get(cellKey(day, slot)) ?? [];
  }

  protected instructorName(e: EntryDto | null, sessionId: string): string {
    const s = this.store.sessions().get(sessionId);
    const id = e?.instructorId ?? s?.fixedInstructorId ?? null;
    return id ? this.store.label(this.store.instructors().get(id)) : '';
  }

  protected roomName(e: EntryDto): string {
    const r = e.roomId ? this.store.rooms().get(e.roomId) : undefined;
    if (!r) return '';
    const label = this.config.name(r) || r.code;
    return r.size ? `${label} · ${this.transloco.translate('timetable.card.capacity', { count: r.size })}` : label;
  }

  protected groupNames(sessionId: string): string {
    const s = this.store.sessions().get(sessionId);
    return (s?.groupIds ?? []).map((g) => this.store.groups().get(g)?.code ?? '').filter(Boolean).join(', ');
  }

  protected violations(e: EntryDto) { return this.store.violationsByEntry().get(e.id) ?? []; }

  protected dimmed(sessionId: string): boolean {
    const s = this.store.sessions().get(sessionId);
    return !!s && this.hiddenTypes().has(s.sessionTypeId);
  }

  protected cellState(day: number, slot: number): 'valid' | 'penalty' | 'invalid' | null {
    if (!this.store.pick()) return null;
    const o = this.store.option(day, slot);
    return o ? (o.status as 'valid' | 'penalty' | 'invalid') : 'invalid';
  }

  protected cellReasons(day: number, slot: number): string {
    return (this.store.option(day, slot)?.reasons ?? []).map((r) => r.message).join('\n');
  }

  protected slotTime(slot: number): string {
    const r = this.rows()[slot];
    return r ? r.start : '';
  }

  // ---- drag and drop ---------------------------------------------------------------------------------

  protected dragStarted(e: CdkDragStart<DragData>): void {
    this.dragging.set(true);
    void this.store.startPick({ sessionId: e.source.data.sessionId, entryId: e.source.data.entryId ?? null });
  }

  protected dragEnded(): void {
    this.dragging.set(false);
    this.hover.set(null);
    // Leave the pick open only when the drop handler is still placing.
    setTimeout(() => { if (!this.store.busy()) this.store.cancelPick(); }, 0);
  }

  protected async droppedOnCell(e: CdkDragDrop<GridCell, unknown, DragData>): Promise<void> {
    if (!this.canEdit()) return;
    await this.store.placeAt(e.container.data);
  }

  protected async droppedOnPanel(e: CdkDragDrop<string, unknown, DragData>): Promise<void> {
    const entry = e.item.data.entryId ? this.store.entries().get(e.item.data.entryId) : undefined;
    this.store.cancelPick();
    if (entry && this.canEdit()) await this.store.unplace(entry);
  }

  // ---- click / keyboard placement ------------------------------------------------------------------

  protected pickUnplaced(u: UnplacedItem): void {
    if (!this.canEdit()) return;
    if (this.store.pick()?.sessionId === u.session.id && !this.store.pick()?.entryId) { this.store.cancelPick(); return; }
    void this.store.startPick({ sessionId: u.session.id });
  }

  protected findAlternative(e: EntryDto): void {
    void this.store.startPick({ sessionId: e.sessionId, entryId: e.id });
    setTimeout(() => this.focusFirstValid(), 300);
  }

  protected beginSwap(e: EntryDto): void {
    this.store.cancelPick();
    this.store.swapFrom.set(e.id);
    this.toast.info('timetable.editor.swapHint');
  }

  protected async cardClicked(e: EntryDto, ev: Event): Promise<void> {
    if (this.store.swapFrom()) { ev.stopPropagation(); await this.store.swapWith(e); }
  }

  protected cardKeydown(e: EntryDto, ev: KeyboardEvent): void {
    if (!this.canEdit()) return;
    if (ev.key === 'Enter' || ev.key === ' ') {
      ev.preventDefault();
      if (this.store.swapFrom()) { void this.store.swapWith(e); return; }
      if (!e.pinned) this.findAlternative(e);
    } else if (ev.key === 'Delete' && !e.pinned) {
      ev.preventDefault();
      void this.store.unplace(e);
    }
  }

  protected async cellClicked(day: number, slot: number): Promise<void> {
    if (!this.store.pick() || this.dragging()) return;
    await this.store.placeAt({ day, slot });
  }

  /** Arrow-key navigation between cells (mirrored in RTL), Enter places the picked session. */
  protected cellKeydown(ev: KeyboardEvent, day: number, slot: number): void {
    const days = this.days();
    const usable = this.rows().filter((r) => !r.isBreak).map((r) => r.slot);
    let d = days.indexOf(day);
    let s = usable.indexOf(slot);
    const rtl = this.lang.isRtl();
    switch (ev.key) {
      case 'ArrowRight': d += rtl ? -1 : 1; break;
      case 'ArrowLeft': d += rtl ? 1 : -1; break;
      case 'ArrowDown': s += 1; break;
      case 'ArrowUp': s -= 1; break;
      case 'Enter': case ' ': ev.preventDefault(); void this.cellClicked(day, slot); return;
      default: return;
    }
    ev.preventDefault();
    d = Math.max(0, Math.min(days.length - 1, d));
    s = Math.max(0, Math.min(usable.length - 1, s));
    this.host.nativeElement.querySelector<HTMLElement>(`#cell-${days[d]}-${usable[s]}`)?.focus();
  }

  private focusFirstValid(): void {
    const el = this.host.nativeElement.querySelector<HTMLElement>('.cell.valid, .cell.penalty');
    el?.focus();
  }

  protected cancel(): void {
    if (this.store.pick() || this.store.swapFrom()) {
      this.store.cancelPick();
      this.store.swapFrom.set(null);
    }
  }

  // ---- schedule actions ----------------------------------------------------------------------------

  protected async autoPlace(): Promise<void> {
    const ids = this.filteredUnplaced().map((u) => u.session.id);
    const r = await this.store.autoPlace(ids);
    if (r) this.toast.success('timetable.editor.autoPlaced', { placed: r.upserted.length, failed: r.failed });
  }

  protected async validate(): Promise<void> {
    const id = this.store.scheduleId();
    if (!id) return;
    try {
      const r = await firstValueFrom(this.api.post<ValidationReportDto>(`/schedules/${id}/validate`));
      this.store.report.set(r);
      this.context.patch(id, { hardViolations: r.hardCount, softScore: r.softPenalty });
      if (r.hardCount === 0) this.toast.success('timetable.editor.validOk', { penalty: r.softPenalty, unplaced: r.unplacedOccurrences });
      else this.toast.warning(null, 'timetable.editor.validHard');
    } catch (e) { this.toast.error(e instanceof ApiError ? e.message : null); }
  }

  protected async publish(): Promise<void> {
    const id = this.store.scheduleId();
    const b = this.store.board();
    if (!id || !b) return;
    const ok = await this.confirm.ask({
      titleKey: 'timetable.editor.publishTitle', messageKey: 'timetable.editor.publishMessage',
      params: { name: b.schedule.name, unplaced: this.store.unplacedTotal() }, confirmKey: 'timetable.editor.publish',
    });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.post<ScheduleSummaryDto>(`/schedules/${id}/publish`));
      this.toast.success('timetable.editor.published', { name: b.schedule.name });
      await this.context.refresh();
      await this.store.load(id);
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : null);
    }
  }

  /** New editable version: a copy of the current schedule, or an empty draft when there is none. */
  protected async newDraft(copy: boolean): Promise<void> {
    try {
      const current = this.context.current();
      let created: ScheduleSummaryDto;
      if (copy && current) {
        created = await firstValueFrom(this.api.post<ScheduleSummaryDto>(`/schedules/${current.id}/clone`, { name: null }));
      } else {
        const termId = current?.termId ?? (await firstValueFrom(this.api.get<PagedResult<TermDto>>('/terms', { pageSize: 100 }))).items
          .sort((a, b) => Number(b.isCurrent) - Number(a.isCurrent))[0]?.id;
        if (!termId) { this.toast.warning(null, 'timetable.editor.noTerm'); return; }
        const name = this.transloco.translate('timetable.editor.draftName', { n: this.context.schedules().length + 1 });
        created = await firstValueFrom(this.api.post<ScheduleSummaryDto>('/schedules', { termId, name }));
      }
      await this.context.refresh();
      this.context.select(created.id);
      this.toast.success('timetable.editor.draftCreated', { name: created.name });
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : null);
    }
  }

  protected trackUnplaced = (_: number, u: UnplacedItem) => u.session.id;
}

function toMinutes(t: string): number {
  const [h, m] = t.split(':').map(Number);
  return h * 60 + (m || 0);
}
