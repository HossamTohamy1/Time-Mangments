import { Injectable, inject, signal } from '@angular/core';
import type { EntryDto } from '../api/models';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthStore } from '../auth/auth.store';
import { ConfigStore } from '../config/config.store';

export interface ScheduleChangedEvent {
  scheduleId: string;
  kind: string;
  upserted?: EntryDto[];
  removed?: string[];
  userId?: string | null;
  userName?: string | null;
  hard?: number;
  soft?: number;
}
export interface NotificationEvent { id: string; type: string; message: string; link?: string | null; }

/** SignalR connection to /hubs/timetable: ConfigChanged, ScheduleChanged and Notification events. */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthStore);
  private readonly config = inject(ConfigStore);
  private hub: HubConnection | null = null;
  private joined: string | null = null;

  readonly connected = signal(false);
  readonly scheduleChanged$ = new Subject<ScheduleChangedEvent>();
  readonly notification$ = new Subject<NotificationEvent>();
  readonly configChanged$ = new Subject<{ area: string }>();

  async connect(): Promise<void> {
    if (this.hub && this.hub.state !== HubConnectionState.Disconnected) return;
    this.hub = new HubConnectionBuilder()
      .withUrl(`/hubs/timetable?institutionId=${this.auth.institutionId() ?? ''}`, { accessTokenFactory: () => this.auth.accessToken() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    this.hub.on('ConfigChanged', (e: { area: string }) => {
      this.configChanged$.next(e);
      void this.config.load();
      if (e.area === 'permissions') void this.auth.loadMe(false);
    });
    this.hub.on('ScheduleChanged', (e: ScheduleChangedEvent) => this.scheduleChanged$.next(e));
    this.hub.on('Notification', (e: NotificationEvent) => this.notification$.next(e));
    this.hub.onreconnected(() => { this.connected.set(true); void this.config.load(); });
    this.hub.onclose(() => this.connected.set(false));
    try {
      await this.hub.start();
      this.connected.set(true);
      this.joined = this.auth.institutionId();
    } catch {
      this.connected.set(false); // the app keeps working without live updates
    }
  }

  async switchInstitution(id: string): Promise<void> {
    if (this.hub?.state === HubConnectionState.Connected) {
      await this.hub.invoke('JoinInstitution', id, this.joined);
      this.joined = id;
    }
  }

  async disconnect(): Promise<void> {
    await this.hub?.stop();
    this.hub = null;
    this.connected.set(false);
  }
}
