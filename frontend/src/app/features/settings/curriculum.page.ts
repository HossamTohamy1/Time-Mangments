import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { SessionGenerationResult } from '../../core/api/models';
import { ToastService } from '../../core/ui/toast.service';
import { RefCache } from '../../shared/forms/ref-cache';
import { LookupNamePipe, NumPipe, TermPipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';
import { EntityListPage } from '../entities/entity-list.page';
import { ENTITY_SCHEMAS } from '../entities/entity-schemas';

/** Curriculum rules + idempotent "Generate sessions" with a preview diff before applying. */
@Component({
  selector: 'app-curriculum-page',
  imports: [TranslocoDirective, EntityListPage, Icon, LookupNamePipe, NumPipe, TermPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-container *transloco="let t">
      <app-entity-list-page [schema]="schema" />
      <div class="tt-page gen">
        <section class="tt-card pad">
          <div class="tt-row">
            <app-icon name="wand" [size]="20" class="accent" />
            <h2>{{ t('settings.curriculum.generate') }}</h2>
            <span class="tt-spacer"></span>
            <select class="tt-input" (change)="termId.set($any($event.target).value)" [attr.aria-label]="t('fields.term')">
              <option value="">{{ t('fields.term') }}…</option>
              @for (term of terms(); track term.id) { <option [value]="term.id">{{ refs.itemLabel(term) }}</option> }
            </select>
            <button type="button" class="tt-btn" [disabled]="!termId() || busy()" (click)="run(false)">{{ t('settings.curriculum.preview') }}</button>
            <button type="button" class="tt-btn primary" [disabled]="!result() || busy() || pending() === 0" (click)="run(true)">{{ t('settings.curriculum.apply') }}</button>
          </div>
          <p class="tt-muted">{{ t('settings.curriculum.hint') }}</p>
          @if (result(); as r) {
            <div class="summary tt-row">
              <span class="tt-chip valid">{{ t('settings.curriculum.created', { n: r.created }) }}</span>
              <span class="tt-chip primary">{{ t('settings.curriculum.updated', { n: r.updated }) }}</span>
              <span class="tt-chip conflict">{{ t('settings.curriculum.removed', { n: r.removed }) }}</span>
              <span class="tt-chip">{{ t('settings.curriculum.unchanged', { n: r.unchanged }) }}</span>
              @if (r.applied) { <span class="tt-chip valid"><app-icon name="check" /> {{ t('settings.curriculum.applied') }}</span> }
            </div>
            <div class="diff">
              <table class="tt-table">
                <thead><tr><th>{{ t('settings.curriculum.action') }}</th><th>{{ 'course' | term }}</th><th>{{ t('fields.sessionType') }}</th><th>{{ 'groups' | term }}</th><th>{{ t('fields.sessionsPerWeek') }}</th></tr></thead>
                <tbody>
                  @for (i of changed(); track $index) {
                    <tr>
                      <td><span class="tt-chip" [class.valid]="i.action === 'create'" [class.conflict]="i.action === 'remove' || i.action === 'blocked'" [class.primary]="i.action === 'update'">{{ t('settings.curriculum.actions.' + i.action) }}</span></td>
                      <td dir="auto">{{ refs.label('/courses', i.courseId) }}</td>
                      <td>{{ i.sessionTypeId | lookupName: 'session-types' }}</td>
                      <td dir="auto">{{ groupLabels(i.groupIds) }}</td>
                      <td>{{ i.sessionsPerWeek | num }}</td>
                    </tr>
                  } @empty { <tr><td colspan="5" class="tt-empty">{{ t('settings.curriculum.nothing') }}</td></tr> }
                </tbody>
              </table>
            </div>
          }
          @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        </section>
      </div>
    </ng-container>
  `,
  styles: [`.gen { padding-block-start: 0; } .pad { padding: 16px; display: flex; flex-direction: column; gap: 10px; } h2 { margin: 0; font-size: 16px; }
    .accent { color: var(--tt-primary); } .diff { max-block-size: 360px; overflow: auto; } .summary { flex-wrap: wrap; } .error { color: var(--tt-conflict); }`],
})
export class CurriculumPage {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  protected readonly refs = inject(RefCache);
  protected readonly schema = ENTITY_SCHEMAS['curriculum-rules'];
  protected readonly terms = computed(() => this.refs.items('/terms'));
  protected readonly termId = signal<string>('');
  protected readonly result = signal<SessionGenerationResult | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly changed = computed(() => (this.result()?.items ?? []).filter((i) => i.action !== 'unchanged'));
  protected readonly pending = computed(() => this.changed().filter((i) => i.action !== 'blocked').length);

  protected groupLabels(ids: string[]): string { return ids.map((g) => this.refs.label('/groups', g)).join(', '); }

  async run(apply: boolean): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const r = await firstValueFrom(this.api.post<SessionGenerationResult>('/curriculum/generate-sessions', { termId: this.termId() }, { preview: !apply }));
      this.result.set(r);
      if (apply) { this.toast.success('settings.curriculum.applied'); this.refs.invalidate('/sessions'); }
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : String(e));
    } finally {
      this.busy.set(false);
    }
  }
}
