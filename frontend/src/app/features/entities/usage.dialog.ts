import { DialogRef, DIALOG_DATA } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';

export interface UsageItem { entity: string; id: string; label?: string | null; }
export interface UsageDialogData { message: string; usages: UsageItem[]; canMerge?: boolean; canDeactivate?: boolean; }
export type UsageDecision = 'merge' | 'deactivate' | undefined;

/** Explains why a delete is blocked and offers safe alternatives (merge / deactivate). */
@Component({
  selector: 'app-usage-dialog',
  imports: [TranslocoDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-dialog" role="alertdialog" *transloco="let t" style="inline-size: min(560px, calc(100vw - 32px))">
      <h2>{{ t('usage.title') }}</h2>
      <p>{{ data.message }}</p>
      <ul class="usages">
        @for (u of data.usages; track $index) {
          <li><span class="tt-chip">{{ t('usage.entity.' + u.entity) }}</span> <span dir="auto">{{ u.label || u.id }}</span></li>
        }
      </ul>
      <div class="tt-dialog-actions">
        <button type="button" class="tt-btn" (click)="ref.close()">{{ t('common.close') }}</button>
        @if (data.canDeactivate) { <button type="button" class="tt-btn" (click)="ref.close('deactivate')">{{ t('usage.deactivate') }}</button> }
        @if (data.canMerge) { <button type="button" class="tt-btn primary" (click)="ref.close('merge')">{{ t('usage.merge') }}</button> }
      </div>
    </div>
  `,
  styles: [`.usages { max-block-size: 260px; overflow: auto; padding-inline-start: 18px; li { margin-block: 4px; } }`],
})
export class UsageDialog {
  protected readonly data = inject<UsageDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<UsageDecision>>(DialogRef);
}
