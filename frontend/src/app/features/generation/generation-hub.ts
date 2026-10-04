import { DestroyRef, Injectable, inject } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthStore } from '../../core/auth/auth.store';

export interface GenerationProgressEvent {
  jobId: string;
  status: string;
  phase: string;
  message: string;
  percent: number;
  objective?: number | null;
  placed?: number | null;
  total?: number | null;
  resultScheduleId?: string | null;
  final: boolean;
}

/** Connection to /hubs/generation, opened while the wizard is on screen. */
@Injectable()
export class GenerationHub {
  private readonly auth = inject(AuthStore);
  private hub: HubConnection | null = null;
  readonly progress$ = new Subject<GenerationProgressEvent>();

  constructor() {
    inject(DestroyRef).onDestroy(() => void this.hub?.stop());
  }

  async connect(): Promise<boolean> {
    if (this.hub && this.hub.state !== HubConnectionState.Disconnected) return true;
    this.hub = new HubConnectionBuilder()
      .withUrl('/hubs/generation', { accessTokenFactory: () => this.auth.accessToken() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    this.hub.on('GenerationProgress', (e: GenerationProgressEvent) => this.progress$.next(e));
    this.hub.onreconnected(() => void this.watch());
    try {
      await this.hub.start();
      await this.watch();
      return true;
    } catch {
      return false; // the wizard falls back to polling
    }
  }

  private async watch(): Promise<void> {
    const id = this.auth.institutionId();
    if (id && this.hub?.state === HubConnectionState.Connected) await this.hub.invoke('Watch', id);
  }
}
