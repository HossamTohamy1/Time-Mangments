import { api, expect, login, test } from './fixtures';

test.beforeAll(async ({ request }) => {
  const a = await api(request);
  const schedules = await a.get('/schedules');
  if (schedules.some((s: { status: string }) => s.status === 'Published')) return;
  const draft = schedules.find((s: { status: string }) => s.status === 'Draft');
  await a.post(`/schedules/${draft.id}/entries/auto-place`, {});
  const res = await a.post(`/schedules/${draft.id}/publish`);
  expect(res.ok()).toBeTruthy();
});

test('an instructor sees their own week and can switch days', async ({ page }) => {
  await login(page, 'dr.ahmed@demo.local');
  await page.goto('/my-timetable');
  await expect(page.locator('.item').first()).toBeVisible();
  await page.locator('.week .tt-btn.icon').last().click();
  await expect(page.locator('.week-label strong')).toBeVisible();
});

test('a student lands on their own timetable and cannot open the editor', async ({ page }) => {
  await login(page, 'student@demo.local');
  await expect(page).toHaveURL(/my-timetable/);
  await page.goto('/timetable');
  await expect(page).toHaveURL(/forbidden/);
});
