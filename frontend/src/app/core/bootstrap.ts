import { inject } from '@angular/core';
import { AuthStore } from './auth/auth.store';
import { ConfigStore } from './config/config.store';
import { RealtimeService } from './realtime/realtime.service';

/** Restores the session from the refresh cookie, loads the effective config and connects live updates. */
export function appBootstrap(): () => Promise<void> {
  // Resolve dependencies synchronously (injection context is lost after the first await).
  const auth = inject(AuthStore);
  const config = inject(ConfigStore);
  const realtime = inject(RealtimeService);
  return () => restore(auth, config, realtime);
}

async function restore(auth: AuthStore, config: ConfigStore, realtime: RealtimeService): Promise<void> {
  if (await auth.tryRestore()) {
    if (auth.me()?.institutionId) {
      await config.load().catch(() => undefined);
      void realtime.connect();
    }
  }
}

/** Shared post-login steps (used by the login page and the institution switcher). */
export async function afterSignIn(auth: AuthStore, config: ConfigStore, realtime: RealtimeService): Promise<void> {
  config.clear();
  if (auth.me()?.institutionId) {
    await config.load(true);
    await realtime.disconnect();
    void realtime.connect();
  }
}
