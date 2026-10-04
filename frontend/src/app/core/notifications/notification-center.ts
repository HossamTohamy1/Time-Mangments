import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Api } from '../api/api';
import type { NotificationDto, NotificationListDto } from '../api/models';
import { RealtimeService } from '../realtime/realtime.service';
import { ToastService } from '../ui/toast.service';
import { ShellStatus } from '../../layout/shell-status';

/** In-app notifications: unread badge, list, live pushes (shown as toasts) and mark-as-read. */
@Injectable({ providedIn: 'root' })
export class NotificationCenter {
  private readonly api = inject(Api);
  private readonly status = inject(ShellStatus);
  private readonly toast = inject(ToastService);
  readonly items = signal<NotificationDto[]>([]);
  private started = false;

  start(): void {
    if (this.started) return;
    this.started = true;
    this.realtime.notification$.subscribe((n) => {
      this.status.unreadNotifications.update((c) => c + 1);
      this.toast.show(n.message);
      void this.refresh();
    });
    void this.refresh();
  }

  private readonly realtime = inject(RealtimeService);

  async refresh(): Promise<void> {
    try {
      const r = await firstValueFrom(this.api.get<NotificationListDto>('/me/notifications'));
      this.items.set(r.items);
      this.status.unreadNotifications.set(r.unread);
    } catch { /* badge is best-effort */ }
  }

  async markRead(ids: string[] = []): Promise<void> {
    await firstValueFrom(this.api.post('/me/notifications/read', ids));
    this.items.update((list) => list.map((n) => (ids.length === 0 || ids.includes(n.id) ? { ...n, isRead: true } : n)));
    this.status.unreadNotifications.set(this.items().filter((n) => !n.isRead).length);
  }

  reset(): void {
    this.items.set([]);
    this.status.unreadNotifications.set(0);
  }
}
