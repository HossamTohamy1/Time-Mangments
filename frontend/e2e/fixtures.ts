import { APIRequestContext, Page, expect, test as base } from '@playwright/test';

export interface Combo { lang: 'en' | 'ar'; theme: 'light' | 'dark'; }

export const PASSWORD = 'Demo#12345';

/** Page pre-configured with the project's language and theme (stored preferences, read by the no-flash script). */
export const test = base.extend<{ combo: Combo; errors: string[] }>({
  combo: async ({}, use, testInfo) => {
    await use(testInfo.project.metadata as Combo);
  },
  errors: async ({ page }, use) => {
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(e.message));
    // Network aborts during navigation (e.g. SignalR negotiation cut short by page.goto) are not application errors.
    const ignored = /Failed to load resource|Failed to complete negotiation|Failed to start the connection|connection was stopped/;
    page.on('console', (m) => { if (m.type() === 'error' && !ignored.test(m.text())) errors.push(m.text()); });
    await use(errors);
  },
  page: async ({ page, combo }, use) => {
    await page.addInitScript(([lang, theme]) => {
      try { localStorage.setItem('tt.lang', lang); localStorage.setItem('tt.wanted-lang', lang); localStorage.setItem('tt.theme', theme); } catch { /* storage blocked */ }
    }, [combo.lang, combo.theme] as const);
    await use(page);
  },
});

export { expect };

/**
 * Signs in. The account's saved language wins after sign-in (it follows the user across devices), so when it differs from
 * the project's language the test switches it with the header toggle — which also exercises the runtime switch.
 */
export async function login(page: Page, email = 'admin@demo.local'): Promise<void> {
  await page.goto('/login');
  await page.getByTestId('login-email').fill(email);
  await page.getByTestId('login-password').fill(PASSWORD);
  await page.getByTestId('login-submit').click();
  await page.waitForURL(/\/(dashboard|my-timetable)/);
  const wanted = await page.evaluate(() => localStorage.getItem('tt.wanted-lang'));
  const lang = wanted ?? 'en';
  if ((await page.locator('html').getAttribute('lang')) !== lang) {
    await page.getByTestId(`lang-${lang}`).click();
    await expect(page.locator('html')).toHaveAttribute('lang', lang);
  }
}

/** Places the pointer on the centre of an on-screen cell the engine marks as valid (green or amber). */
export async function visibleValidCell(page: Page): Promise<{ x: number; y: number }> {
  await expect(page.locator('.cell.valid, .cell.penalty').first()).toBeAttached();
  const point = await page.evaluate(() => {
    const view = document.querySelector('.board-scroll')!.getBoundingClientRect();
    for (const el of Array.from(document.querySelectorAll('.cell.valid, .cell.penalty'))) {
      const r = el.getBoundingClientRect();
      if (r.top >= view.top && r.bottom <= view.bottom && r.left >= view.left && r.right <= view.right) return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    }
    return null;
  });
  if (!point) throw new Error('no valid cell on screen');
  return point;
}

/** API helper for arranging data (token + institution header). */
export async function api(request: APIRequestContext, institutionCode = 'UNI', email = 'admin@demo.local') {
  const res = await request.post('/api/v1/auth/login', { data: { email, password: PASSWORD } });
  const { accessToken } = await res.json();
  const me = await (await request.get('/api/v1/auth/me', { headers: { authorization: `Bearer ${accessToken}` } })).json();
  const inst = me.institutions.find((i: { code: string }) => i.code === institutionCode)?.institutionId;
  const headers = { authorization: `Bearer ${accessToken}`, 'X-Institution-Id': inst };
  return {
    get: async (path: string) => (await request.get('/api/v1' + path, { headers })).json(),
    post: async (path: string, data: unknown = {}) => request.post('/api/v1' + path, { headers, data }),
  };
}

/** Opens the editor on the seeded "Draft v1" (other specs create newer drafts). */
export async function openDraft(page: Page): Promise<void> {
  await page.goto('/timetable');
  const select = page.locator('.schedule-select select');
  await expect(select.locator('option').first()).toBeAttached();
  const value = await select.locator('option').evaluateAll((opts) => (opts as HTMLOptionElement[]).find((o) => o.textContent?.startsWith('Draft v1'))?.value);
  if (value) await select.selectOption(value);
  await expect(page.locator('.u-item').first()).toBeVisible();
}
