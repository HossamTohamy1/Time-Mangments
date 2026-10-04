import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { ConstraintSeverity, ConstraintSummaryDto, ParameterSchemaDto } from '../../core/api/models';
import { ConfigStore } from '../../core/config/config.store';
import { ToastService } from '../../core/ui/toast.service';
import { Icon } from '../../shared/ui/icon';
import { RuleBuilderSection } from './rule-builder.section';
import { ImpactService } from '../../shared/impact/impact';

interface Draft { severity: ConstraintSeverity; weight: number; params: Record<string, unknown>; dirty: boolean; error?: string; }

/** Constraint catalogue tuning per institution (enable/disable, Hard/Soft, weight, parameters) + Rule Builder. */
@Component({
  selector: 'app-constraints-page',
  imports: [TranslocoDirective, FormsModule, Icon, RuleBuilderSection],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './constraints.page.html',
  styleUrl: './constraints.page.scss',
})
export class ConstraintsPage {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly impact = inject(ImpactService);
  protected readonly config = inject(ConfigStore);
  protected readonly drafts = signal<Record<string, Draft>>({});
  protected readonly severities: ConstraintSeverity[] = ['Hard', 'Soft', 'Off'];

  protected readonly groups = computed(() => {
    const list = (this.config.config()?.constraints ?? []).filter((c) => !c.requiresFeature || this.config.feature(c.requiresFeature));
    const byCat = new Map<string, ConstraintSummaryDto[]>();
    for (const c of list) byCat.set(c.category, [...(byCat.get(c.category) ?? []), c]);
    return [...byCat.entries()];
  });

  protected draft(c: ConstraintSummaryDto): Draft {
    return this.drafts()[c.code] ?? { severity: c.severity, weight: c.weight, params: JSON.parse(c.parametersJson || '{}'), dirty: false };
  }

  protected change(c: ConstraintSummaryDto, patch: Partial<Draft>): void {
    this.drafts.update((d) => ({ ...d, [c.code]: { ...this.draft(c), ...patch, dirty: true, error: undefined } }));
  }

  protected param(c: ConstraintSummaryDto, p: ParameterSchemaDto): unknown {
    const v = this.draft(c).params[p.name];
    return v ?? p.default ?? (p.type === 'Bool' ? false : p.type.endsWith('List') ? [] : null);
  }

  protected setParam(c: ConstraintSummaryDto, p: ParameterSchemaDto, raw: unknown): void {
    let v: unknown = raw;
    if (p.type === 'Int' || p.type === 'Decimal') v = raw === '' || raw === null ? undefined : Number(raw);
    if (p.type === 'StringList') v = String(raw).split(',').map((x) => x.trim()).filter(Boolean);
    if (p.type === 'IntList') v = String(raw).split(',').map((x) => Number(x.trim())).filter((x) => !Number.isNaN(x));
    const params = { ...this.draft(c).params };
    if (v === undefined || (Array.isArray(v) && v.length === 0)) delete params[p.name]; else params[p.name] = v;
    this.change(c, { params });
  }

  protected listText(c: ConstraintSummaryDto, p: ParameterSchemaDto): string {
    const v = this.param(c, p);
    return Array.isArray(v) ? v.join(', ') : '';
  }

  protected toggleCode(c: ConstraintSummaryDto, p: ParameterSchemaDto, code: string): void {
    const list = [...((this.param(c, p) as string[]) ?? [])];
    const i = list.indexOf(code);
    if (i >= 0) list.splice(i, 1); else list.push(code);
    this.setParam(c, p, list.join(','));
  }

  async save(c: ConstraintSummaryDto): Promise<void> {
    const d = this.draft(c);
    try {
      const body = { severity: d.severity, weight: d.weight, parameters: d.params };
      if (!(await this.impact.confirm(`/config/constraints/${c.code}/impact`, body))) return;
      await firstValueFrom(this.api.put(`/config/constraints/${c.code}`, { severity: d.severity, weight: d.weight, parameters: d.params }));
      this.drafts.update((x) => { const n = { ...x }; delete n[c.code]; return n; });
      await this.config.load(true);
      this.toast.success('common.saved');
    } catch (e) {
      const msg = e instanceof ApiError ? Object.values(e.problem.errors ?? {})[0]?.[0]?.message ?? e.message : String(e);
      this.drafts.update((x) => ({ ...x, [c.code]: { ...d, error: msg } }));
    }
  }

  protected revert(c: ConstraintSummaryDto): void {
    this.drafts.update((x) => { const n = { ...x }; delete n[c.code]; return n; });
  }
}
