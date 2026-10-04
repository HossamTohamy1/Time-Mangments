import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { TranslocoDirective } from '@jsverse/transloco';
import type { NotificationDto } from '../../core/api/models';
import { NotificationCenter } from '../../core/notifications/notification-center';
import { LocalDatePipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';

/** Notification inbox (messages are localized by the server in the user's language). */
@Component({
  selector: 'app-notifications-page',
  imports: [TranslocoDirective, Icon, LocalDatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head">
        <app-icon name="bell" [size]="22" class="head-icon" />
        <h1>{{ t('notifications.title') }}</h1>
        <span class="tt-spacer"></span>
        <button type="button" class="tt-btn" [disabled]="center.items().every((n) => n.isRead)" (click)="center.markRead()"><app-icon name="check" /> {{ t('notifications.markAll') }}</button>
      </div>
      <ul class="tt-card list">
        @for (n of center.items(); track n.id) {
          <li [class.unread]="!n.isRead">
            <button type="button" (click)="open(n)">
              <span class="dot" aria-hidden="true"></span>
              <span class="msg" dir="auto">{{ n.message }}</span>
              <span class="when">{{ n.createdAt | localDate:'datetime' }}</span>
            </button>
          </li>
        } @empty {
          <li class="tt-empty">{{ t('notifications.none') }}</li>
        }
      </ul>
    </div>
  `,
  styles: [`
    .head-icon { color: var(--tt-primary); }
    .list { list-style: none; margin: 0; padding: 0; overflow: hidden; }
    li { border-block-end: 1px solid var(--tt-border); &:last-child { border-block-end: 0; } }
    li button { inline-size: 100%; display: flex; align-items: center; gap: 12px; padding: 14px 16px; background: transparent; border: 0; color: var(--tt-on-surface); text-align: start; cursor: pointer;
      &:hover { background: var(--tt-surface-variant); } }
    .dot { inline-size: 8px; block-size: 8px; border-radius: 50%; background: transparent; flex-shrink: 0; }
    li.unread .dot { background: var(--tt-primary); }
    li.unread .msg { font-weight: 600; }
    .msg { flex: 1; }
    .when { font-size: 12px; color: var(--tt-on-surface-muted); white-space: nowrap; }
  `],
})
export class NotificationsPage {
  protected readonly center = inject(NotificationCenter);
  private readonly router = inject(Router);

  constructor() { void this.center.refresh(); }

  protected async open(n: NotificationDto): Promise<void> {
    if (!n.isRead) await this.center.markRead([n.id]);
    if (n.link) void this.router.navigateByUrl(n.link);
  }
}
