import { expect, login, test } from './fixtures';

test('downloads the timetable as PDF', async ({ page, combo }) => {
  test.skip(combo.theme === 'dark', 'format check only');
  await login(page);
  await page.goto('/exports');
  const [download] = await Promise.all([page.waitForEvent('download'), page.locator('.big').click()]);
  expect(download.suggestedFilename()).toMatch(/\.pdf$/);
});
