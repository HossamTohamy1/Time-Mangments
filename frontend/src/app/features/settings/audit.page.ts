import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, PagedResult } from '../../core/api/api';
import type { AuditEntryDto } from '../../core/api/models';
import { LocalDatePipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';

/** Configuration audit trail (who, when, before, after). */
@Component({
  selector: 'app-audit-page',
  imports: [TranslocoDirective, Icon, LocalDatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head"><app-icon name="history" [size]="22" class="head-icon" /><h1>{{ t('settings.audit.title') }}</h1></div>
      <div class="tt-card wrap">
        <table class="tt-table">
          <thead><tr><th>{{ t('settings.audit.when') }}</th><th>{{ t('settings.audit.who') }}</th><th>{{ t('settings.audit.what') }}</th><th>{{ t('settings.curriculum.action') }}</th><th></th></tr></thead>
          <tbody>
            @for (a of rows(); track a.id) {
              <tr>
                <td>{{ a.at | localDate: 'datetime' }}</td><td dir="ltr">{{ a.userName }}</td><td>{{ a.entityType }}</td>
                <td><span class="tt-chip">{{ t('enums.changeAction.' + a.action) }}</span></td>
                <td><button type="button" class="tt-btn sm ghost" (click)="open.set(open() === a.id ? null : a.id)">{{ t('settings.audit.details') }}</button></td>
              </tr>
              @if (open() === a.id) {
                <tr><td colspan="5"><div class="diff"><pre dir="ltr">{{ a.beforeJson }}</pre><pre dir="ltr">{{ a.afterJson }}</pre></div></td></tr>
              }
            }
          </tbody>
        </table>
      </div>
      <div class="tt-row" style="justify-content:center">
        <button type="button" class="tt-btn sm" [disabled]="page() <= 1" (click)="go(page() - 1)">{{ t('common.prev') }}</button>
        <button type="button" class="tt-btn sm" [disabled]="page() * 50 >= total()" (click)="go(page() + 1)">{{ t('common.next') }}</button>
      </div>
    </div>
  `,
  styles: [`.head-icon { color: var(--tt-primary); } .wrap { overflow: auto; } .diff { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    pre { margin: 0; font-size: 11px; white-space: pre-wrap; word-break: break-all; background: var(--tt-surface-variant); padding: 8px; border-radius: 6px; max-block-size: 220px; overflow: auto; }`],
})
export class AuditPage {
  private readonly api = inject(Api);
  protected readonly rows = signal<AuditEntryDto[]>([]);
  protected readonly page = signal(1);
  protected readonly total = signal(0);
  protected readonly open = signal<string | null>(null);

  constructor() { void this.go(1); }

  async go(p: number): Promise<void> {
    const res = await firstValueFrom(this.api.get<PagedResult<AuditEntryDto>>('/config/audit', { page: p, pageSize: 50 }));
    this.rows.set(res.items);
    this.total.set(res.total);
    this.page.set(p);
  }
}
