import { expect, login, test } from './fixtures';

test('signs in and renders the shell in the chosen language and theme', async ({ page, combo, errors }) => {
  await login(page);
  const html = page.locator('html');
  await expect(html).toHaveAttribute('dir', combo.lang === 'ar' ? 'rtl' : 'ltr');
  await expect(html).toHaveAttribute('lang', combo.lang);
  await expect(html).toHaveAttribute('data-theme', combo.theme);
  await expect(page.locator('nav').first()).toBeVisible();
  await expect(page.locator('h1').first()).toBeVisible();

  for (const path of ['/rooms', '/settings/constraints', '/conflicts', '/schedules', '/exports']) {
    await page.goto(path);
    await expect(page.locator('h1').first()).toBeVisible();
  }
  expect(errors).toEqual([]);
});
