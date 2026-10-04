import { Dialog, DialogRef, DIALOG_DATA } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, Injectable, inject } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api } from '../../core/api/api';
import type { ImpactDto } from '../../core/api/models';
import { NumPipe } from '../pipes/pipes';
import { Icon } from '../ui/icon';

/** Shows what a configuration change would break in existing schedules, before applying it. */
@Component({
  selector: 'app-impact-dialog',
  imports: [TranslocoDirective, NumPipe, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-dialog" role="alertdialog" *transloco="let t" style="inline-size: min(720px, calc(100vw - 32px))">
      <h2><app-icon name="alert" class="warn" /> {{ t('impact.title') }}</h2>
      <p class="tt-muted">{{ t('impact.subtitle', { violations: data.newHardViolations, entries: data.affectedEntries }) }}</p>
      <table class="tt-table">
        <thead><tr><th>{{ t('impact.schedule') }}</th><th>{{ t('impact.hard') }}</th><th>{{ t('impact.soft') }}</th><th>{{ t('impact.entries') }}</th></tr></thead>
        <tbody>
          @for (s of data.schedules; track s.scheduleId) {
            <tr>
              <td dir="auto">{{ s.name }} <span class="tt-chip">{{ t('schedules.status.' + s.status) }}</span></td>
              <td><span [class.bad]="s.hardAfter > s.hardBefore">{{ s.hardBefore | num }} → {{ s.hardAfter | num }}</span></td>
              <td>{{ s.softBefore | num }} → {{ s.softAfter | num }}</td>
              <td>{{ s.newlyInvalidEntryIds.length | num }}</td>
            </tr>
          }
        </tbody>
      </table>
      @if (examples().length > 0) {
        <h3>{{ t('impact.examples') }}</h3>
        <ul class="examples">@for (v of examples(); track $index) { <li dir="auto">{{ v.message }}</li> }</ul>
      }
      <div class="tt-dialog-actions">
        <button type="button" class="tt-btn" (click)="ref.close(false)">{{ t('common.cancel') }}</button>
        <button type="button" class="tt-btn danger" (click)="ref.close(true)" data-testid="impact-apply">{{ t('impact.applyAnyway') }}</button>
      </div>
    </div>
  `,
  styles: [`.warn { color: var(--tt-penalty); } .bad { color: var(--tt-conflict); font-weight: 600; } h3 { font-size: 14px; margin: 14px 0 6px; }
    .examples { max-block-size: 180px; overflow: auto; padding-inline-start: 18px; font-size: 13px; }`],
})
export class ImpactDialog {
  protected readonly data = inject<ImpactDto>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  protected examples() { return this.data.schedules.flatMap((s) => s.newViolations).slice(0, 12); }
}

/** Runs an impact endpoint and asks for confirmation only when the change would introduce new hard violations. */
@Injectable({ providedIn: 'root' })
export class ImpactService {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);

  async confirm(path: string, body: unknown): Promise<boolean> {
    const impact = await firstValueFrom(this.api.post<ImpactDto>(path, body));
    if (impact.newHardViolations <= 0 && impact.affectedEntries <= 0) return true;
    const ref = this.dialog.open<boolean>(ImpactDialog, { data: impact });
    return (await firstValueFrom(ref.closed)) === true;
  }
}
