import { expect, login, test } from './fixtures';

test('generation wizard produces a timetable without hard conflicts', async ({ page, combo }) => {
  test.skip(combo.theme === 'dark', 'one run per language is enough for the solver');
  await login(page);
  await page.goto('/generation');
  await page.locator('.actions .tt-btn.primary').click();
  await page.locator('.choice').nth(2).click(); // fast heuristic keeps the e2e run short
  await page.locator('.actions .tt-btn.primary').click();
  await expect(page.locator('.kpis').first()).toBeVisible();
  await page.locator('.actions .tt-btn.primary').click();
  await expect(page.locator('.kpis.result')).toBeVisible({ timeout: 60_000 });
  await expect(page.locator('.kpi.ok strong')).toHaveText(/^[0٠]$/);
});
