/** Application bootstrap hook (session restore + effective config load are added by the auth/config stores). */
export async function appBootstrap(): Promise<void> {
  return Promise.resolve();
}
