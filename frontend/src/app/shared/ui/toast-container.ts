import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { ToastService } from '../../core/ui/toast.service';
import { Icon } from './icon';

@Component({
  selector: 'app-toast-container',
  imports: [TranslocoDirective, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toasts" role="status" aria-live="polite" *transloco="let t">
      @for (toast of toasts.toasts(); track toast.id) {
        <div class="toast" [class]="toast.kind">
          <app-icon [name]="toast.kind === 'success' ? 'checkCircle' : toast.kind === 'error' ? 'alertCircle' : toast.kind === 'warning' ? 'alert' : 'info'" />
          <span class="text">{{ toast.text ?? t(toast.key ?? '', toast.params ?? {}) }}</span>
          <button type="button" class="close" [attr.aria-label]="t('common.close')" (click)="toasts.dismiss(toast.id)"><app-icon name="close" /></button>
        </div>
      }
    </div>
  `,
  styles: [`
    .toasts { position: fixed; inset-block-end: 16px; inset-inline-end: 16px; z-index: 1000; display: flex; flex-direction: column; gap: 8px; max-inline-size: min(420px, calc(100vw - 32px)); }
    .toast { display: flex; align-items: flex-start; gap: 10px; padding: 12px 14px; border-radius: 10px; background: var(--tt-surface); color: var(--tt-on-surface);
      border: 1px solid var(--tt-border); border-inline-start: 4px solid var(--tt-info); box-shadow: var(--tt-shadow-lg); }
    .toast.success { border-inline-start-color: var(--tt-valid); app-icon { color: var(--tt-valid); } }
    .toast.error { border-inline-start-color: var(--tt-conflict); app-icon { color: var(--tt-conflict); } }
    .toast.warning { border-inline-start-color: var(--tt-penalty); app-icon { color: var(--tt-penalty); } }
    .text { flex: 1; line-height: 1.4; }
    .close { border: 0; background: transparent; color: var(--tt-on-surface-muted); cursor: pointer; padding: 0; }
  `],
})
export class ToastContainer {
  protected readonly toasts = inject(ToastService);
}
