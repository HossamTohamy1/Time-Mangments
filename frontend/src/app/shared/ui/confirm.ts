import { Dialog, DialogRef, DIALOG_DATA } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, Injectable, inject } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';

export interface ConfirmData { titleKey: string; messageKey?: string; message?: string; params?: Record<string, unknown>; confirmKey?: string; danger?: boolean; }

@Component({
  selector: 'app-confirm-dialog',
  imports: [TranslocoDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-dialog" role="alertdialog" aria-modal="true" *transloco="let t">
      <h2>{{ t(data.titleKey, data.params ?? {}) }}</h2>
      @if (data.message || data.messageKey) {
        <p class="tt-muted">{{ data.message ?? t(data.messageKey!, data.params ?? {}) }}</p>
      }
      <div class="tt-dialog-actions">
        <button type="button" class="tt-btn" (click)="ref.close(false)">{{ t('common.cancel') }}</button>
        <button type="button" class="tt-btn" [class.primary]="!data.danger" [class.danger]="data.danger" (click)="ref.close(true)" cdkFocusInitial>
          {{ t(data.confirmKey ?? 'common.confirm') }}
        </button>
      </div>
    </div>
  `,
})
export class ConfirmDialog {
  protected readonly data = inject<ConfirmData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
}

@Injectable({ providedIn: 'root' })
export class ConfirmService {
  private readonly dialog = inject(Dialog);

  async ask(data: ConfirmData): Promise<boolean> {
    const ref = this.dialog.open<boolean>(ConfirmDialog, { data, width: '420px', maxWidth: 'calc(100vw - 32px)' });
    return (await firstValueFrom(ref.closed)) === true;
  }
}
