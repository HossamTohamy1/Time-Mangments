import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Subject } from 'rxjs';
import { Api } from '../../core/api/api';
import type { BoardSessionDto, EntryDto, ScheduleBoardDto } from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { ScheduleContext } from '../../core/schedule/schedule-context';
import { ToastService } from '../../core/ui/toast.service';
import { BoardStore, inWeek } from './board.store';

const session = (id: string, groupIds: string[], perWeek = 1, instructor: string | null = null): BoardSessionDto => ({
  id, courseId: 'c-' + id, courseCode: id.toUpperCase(), nameEn: id, nameAr: null, sessionTypeId: 't', duration: 1, perWeek, groupIds,
  fixedInstructorId: instructor, instructorOptions: instructor ? [instructor] : [], requiredRoomTypeId: null, weekMask: 0, studentCount: 30, requiresRoom: true,
});

const entry = (id: string, sessionId: string, day: number, slot: number, weekMask = 0, instructorId: string | null = null): EntryDto => ({
  id, sessionId, occurrence: 0, day, startSlot: slot, duration: 1, roomId: 'r1', instructorId, weekMask, pinned: false, rowVersion: 'AA==',
});

// Cohort Y3 with sections A and B; section A has lab group A1.
const board = {
  schedule: { id: 's', termId: 't', name: 'Draft', version: 1, status: 'Draft', locked: false, entryCount: 3, createdAt: '' },
  editable: true, canUndo: false, canRedo: false,
  groups: [
    { id: 'y3', code: 'Y3', parentId: null, size: 60 }, { id: 'a', code: 'A', parentId: 'y3', size: 30 },
    { id: 'b', code: 'B', parentId: 'y3', size: 30 }, { id: 'a1', code: 'A1', parentId: 'a', size: 15 },
  ],
  instructors: [{ id: 'i1', code: 'I1', size: 0 }], rooms: [{ id: 'r1', code: 'R1', size: 40 }],
  sessions: [session('lecture', ['y3'], 2, 'i1'), session('secA', ['a']), session('secB', ['b']), session('labA1', ['a1'])],
  entries: [entry('e1', 'lecture', 0, 0, 0, 'i1'), entry('e2', 'secA', 1, 0, 1), entry('e3', 'secB', 1, 1, 2)],
} as unknown as ScheduleBoardDto;

describe('BoardStore', () => {
  let store: BoardStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        BoardStore,
        { provide: Api, useValue: {} },
        { provide: ToastService, useValue: { info: () => undefined, warning: () => undefined, error: () => undefined } },
        { provide: AuthStore, useValue: { me: signal(null) } },
        { provide: ConfigStore, useValue: { name: (x: { nameEn?: string }) => x?.nameEn ?? '' } },
        { provide: ScheduleContext, useValue: { patch: () => undefined } },
        { provide: RealtimeService, useValue: { scheduleChanged$: new Subject() } },
      ],
    });
    store = TestBed.inject(BoardStore);
    store.board.set(board);
    store.entries.set(new Map(board.entries.map((e) => [e.id, e])));
  });

  it('week masks: 0 means every week', () => {
    expect(inWeek(0, 1)).toBe(true);
    expect(inWeek(1, 0)).toBe(true);
    expect(inWeek(1, 1)).toBe(false);
    expect(inWeek(2, 1)).toBe(true);
  });

  it('a section sees its cohort lectures and its own sessions, not the sibling section', () => {
    store.setView('group', 'a');
    store.week.set(0);
    expect(store.visibleEntries().map((e) => e.id).sort()).toEqual(['e1', 'e2']);
    store.week.set(1);
    expect(store.visibleEntries().map((e) => e.id)).toEqual(['e1']);
  });

  it('unplaced lists missing occurrences relevant to the selection', () => {
    store.setView('group', 'a');
    const unplaced = store.unplaced().map((u) => [u.session.id, u.missing]);
    expect(unplaced).toEqual([['lecture', 1], ['labA1', 1]]);
    expect(store.unplacedTotal()).toBe(2);
  });

  it('instructor view shows only that instructor\'s entries', () => {
    store.setView('instructor', 'i1');
    expect(store.visibleEntries().map((e) => e.id)).toEqual(['e1']);
  });

  it('applies conflict reports to entries', () => {
    store.report.set({ scheduleId: 's', hardCount: 1, softPenalty: 0, unplacedOccurrences: 0, violations: [
      { constraintCode: 'X', code: 'X', severity: 'Hard', penalty: 0, message: 'm', params: {}, entities: [{ kind: 'Entry', id: 'e2' }] },
    ] });
    expect(store.violationsByEntry().get('e2')?.length).toBe(1);
    expect(store.hardCount()).toBe(1);
  });
});
