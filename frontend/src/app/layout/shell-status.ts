import { Injectable, signal } from '@angular/core';

/** Cross-feature header/sidebar status (conflict badge, current schedule pill). Features update it. */
@Injectable({ providedIn: 'root' })
export class ShellStatus {
  readonly conflictCount = signal<number | null>(null);
  readonly scheduleLabel = signal<string | null>(null);
  readonly unreadNotifications = signal(0);
}
