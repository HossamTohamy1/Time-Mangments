import { DialogRef, DIALOG_DATA } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { NgTemplateOutlet } from '@angular/common';
import { TranslocoDirective } from '@jsverse/transloco';
import { catchError, debounceTime, firstValueFrom, of, switchMap } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { ConstraintSeverity, RuleDto, RulePreviewDto } from '../../core/api/models';
import { ConfigStore, LookupKind } from '../../core/config/config.store';
import { ImpactService } from '../../shared/impact/impact';
import { DayNamePipe, LocalNamePipe, NumPipe, TermPipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';

export interface RuleScope {
  sessionTypeCodes: string[]; courseTags: string[]; sessionTags: string[]; instructorTypeCodes: string[]; groupKindCodes: string[];
  roomTypeCodes: string[]; shiftCodes: string[]; courseIds: string[]; orgUnitIds: string[]; instructorIds: string[]; groupIds: string[];
}
export interface RuleCondition {
  type: string; per: 'group' | 'instructor' | 'room'; max?: number | null; slots?: number | null; from?: number | null; to?: number | null;
  days: number[]; otherScope?: RuleScope | null; sameCourse: boolean;
}
export interface RuleModel { scope: RuleScope; condition: RuleCondition; effect: 'forbid' | 'limit' | 'prefer'; }

const emptyScope = (): RuleScope => ({ sessionTypeCodes: [], courseTags: [], sessionTags: [], instructorTypeCodes: [], groupKindCodes: [], roomTypeCodes: [],
  shiftCodes: [], courseIds: [], orgUnitIds: [], instructorIds: [], groupIds: [] });

export const CONDITION_TYPES = ['maxPerDay', 'maxPerWeek', 'minGap', 'slotRange', 'days', 'notAdjacent', 'before', 'after', 'sameRoom', 'sameDay', 'preferredTime', 'avoidTime'];
const USES_PER = new Set(['maxPerDay', 'maxPerWeek', 'minGap', 'notAdjacent', 'before', 'after', 'sameRoom', 'sameDay']);
const USES_RANGE = new Set(['slotRange', 'preferredTime', 'avoidTime']);
const USES_DAYS = new Set(['days', 'preferredTime', 'avoidTime']);
const USES_OTHER = new Set(['notAdjacent', 'before', 'after']);

/** No-code Rule Builder: scope + condition + effect, with live preview and impact analysis before saving. */
@Component({
  selector: 'app-rule-editor-dialog',
  imports: [TranslocoDirective, FormsModule, NgTemplateOutlet, Icon, LocalNamePipe, NumPipe, DayNamePipe, TermPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './rule-editor.dialog.html',
  styleUrl: './rule-editor.dialog.scss',
})
export class RuleEditorDialog {
  protected readonly input = inject<{ rule: RuleDto | null; scheduleId: string | null }>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly api = inject(Api);
  private readonly impact = inject(ImpactService);
  protected readonly config = inject(ConfigStore);

  protected readonly conditionTypes = CONDITION_TYPES;
  protected readonly allDays = [0, 1, 2, 3, 4, 5, 6];
  protected readonly code = signal(this.input.rule?.code ?? '');
  protected readonly nameEn = signal(this.input.rule?.nameEn ?? '');
  protected readonly nameAr = signal(this.input.rule?.nameAr ?? '');
  protected readonly severity = signal<ConstraintSeverity>(this.input.rule?.severity ?? 'Hard');
  protected readonly weight = signal(this.input.rule?.weight ?? 5);
  protected readonly model = signal<RuleModel>(this.normalize(this.input.rule?.definition as unknown as Partial<RuleModel> | undefined));
  protected readonly preview = signal<RulePreviewDto | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly cond = computed(() => this.model().condition);
  protected readonly usesPer = computed(() => USES_PER.has(this.cond().type));
  protected readonly usesRange = computed(() => USES_RANGE.has(this.cond().type));
  protected readonly usesDays = computed(() => USES_DAYS.has(this.cond().type));
  protected readonly usesOther = computed(() => USES_OTHER.has(this.cond().type));
  protected readonly shifts = computed(() => this.config.time()?.shifts ?? []);

  constructor() {
    toObservable(computed(() => ({ m: this.model(), s: this.severity(), w: this.weight() }))).pipe(
      debounceTime(400),
      switchMap(({ m, s, w }) => this.api.post<RulePreviewDto>('/rules/preview', { definition: this.clean(m), severity: s, weight: w, scheduleId: this.input.scheduleId })
        .pipe(catchError(() => of(null)))),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe((p) => this.preview.set(p));
  }

  private normalize(d: Partial<RuleModel> | undefined): RuleModel {
    return {
      scope: { ...emptyScope(), ...(d?.scope ?? {}) },
      condition: { type: 'maxPerDay', per: 'group', max: 2, days: [], sameCourse: true, ...(d?.condition ?? {}),
        otherScope: d?.condition?.otherScope ? { ...emptyScope(), ...d.condition.otherScope } : null },
      effect: d?.effect ?? 'limit',
    };
  }

  /** Drops empty filters so the stored JSON stays readable. */
  private clean(m: RuleModel): unknown {
    const scope = (s: RuleScope) => Object.fromEntries(Object.entries(s).filter(([, v]) => Array.isArray(v) && v.length > 0));
    const c = m.condition;
    return {
      scope: scope(m.scope),
      condition: {
        type: c.type, per: c.per, ...(c.max !== null && c.max !== undefined ? { max: c.max } : {}), ...(c.slots ? { slots: c.slots } : {}),
        ...(c.from ? { from: c.from } : {}), ...(c.to ? { to: c.to } : {}), days: c.days,
        ...(USES_OTHER.has(c.type) ? { otherScope: scope(c.otherScope ?? emptyScope()), sameCourse: c.sameCourse } : {}),
      },
      effect: m.effect,
    };
  }

  protected setCond(patch: Partial<RuleCondition>): void {
    this.model.update((m) => ({ ...m, condition: { ...m.condition, ...patch, otherScope: patch.type && USES_OTHER.has(patch.type) ? m.condition.otherScope ?? emptyScope() : patch.otherScope ?? m.condition.otherScope } }));
    if (patch.type === 'preferredTime' || patch.type === 'avoidTime') { this.model.update((m) => ({ ...m, effect: 'prefer' })); this.severity.set('Soft'); }
  }

  protected toggle(target: 'scope' | 'other', key: keyof RuleScope, value: string): void {
    this.model.update((m) => {
      const s = { ...(target === 'scope' ? m.scope : m.condition.otherScope ?? emptyScope()) };
      const list = [...(s[key] as string[])];
      const i = list.indexOf(value);
      if (i >= 0) list.splice(i, 1); else list.push(value);
      (s[key] as string[]) = list;
      return target === 'scope' ? { ...m, scope: s } : { ...m, condition: { ...m.condition, otherScope: s } };
    });
  }

  protected has(target: 'scope' | 'other', key: keyof RuleScope, value: string): boolean {
    const s = target === 'scope' ? this.model().scope : this.model().condition.otherScope;
    return ((s?.[key] as string[]) ?? []).includes(value);
  }

  protected setTags(target: 'scope' | 'other', key: 'courseTags' | 'sessionTags', text: string): void {
    const tags = text.split(/[,،]/).map((x) => x.trim()).filter(Boolean);
    this.model.update((m) => target === 'scope'
      ? { ...m, scope: { ...m.scope, [key]: tags } }
      : { ...m, condition: { ...m.condition, otherScope: { ...(m.condition.otherScope ?? emptyScope()), [key]: tags } } });
  }

  protected toggleDay(d: number): void {
    const days = this.cond().days.includes(d) ? this.cond().days.filter((x) => x !== d) : [...this.cond().days, d];
    this.setCond({ days });
  }

  protected lookups(kind: LookupKind) { return this.config.lookups(kind); }

  async save(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    const body = { code: this.code(), nameEn: this.nameEn(), nameAr: this.nameAr(), severity: this.severity(), weight: this.weight(), definition: this.clean(this.model()) };
    try {
      if (this.severity() !== 'Off' && !(await this.impact.confirm('/rules/impact', body))) return;
      const id = this.input.rule?.id;
      if (id) await firstValueFrom(this.api.put(`/rules/${id}`, body)); else await firstValueFrom(this.api.post('/rules', body));
      this.ref.close(true);
    } catch (e) {
      this.error.set(e instanceof ApiError ? Object.values(e.problem.errors ?? {})[0]?.[0]?.message ?? e.message : String(e));
    } finally {
      this.busy.set(false);
    }
  }
}
