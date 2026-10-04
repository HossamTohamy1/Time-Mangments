import { DialogRef, DIALOG_DATA } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import type { LookupDto } from '../../core/api/models';
import { LocalNamePipe } from '../../shared/pipes/pipes';

@Component({
  selector: 'app-merge-dialog',
  imports: [TranslocoDirective, LocalNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-dialog" *transloco="let t" style="inline-size: min(460px, calc(100vw - 32px))">
      <h2>{{ t('settings.lookups.mergeTitle', { code: data.source.code }) }}</h2>
      <p class="tt-muted">{{ t('settings.lookups.mergeText') }}</p>
      <label class="tt-field">{{ t('settings.lookups.mergeInto') }}
        <select class="tt-input" (change)="target.set($any($event.target).value)">
          <option value="">—</option>
          @for (o of data.options; track o.id) { <option [value]="o.id">{{ o.code }} · {{ o | localName }}</option> }
        </select>
      </label>
      <div class="tt-dialog-actions">
        <button type="button" class="tt-btn" (click)="ref.close()">{{ t('common.cancel') }}</button>
        <button type="button" class="tt-btn primary" [disabled]="!target()" (click)="ref.close(target()!)">{{ t('usage.merge') }}</button>
      </div>
    </div>
  `,
})
export class MergeDialog {
  protected readonly data = inject<{ source: LookupDto; options: LookupDto[] }>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<string>>(DialogRef);
  protected readonly target = signal<string | null>(null);
}
