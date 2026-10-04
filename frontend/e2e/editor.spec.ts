import { expect, login, openDraft, test, visibleValidCell } from './fixtures';

/** Drag an unplaced session onto a cell the engine marks as valid; works mirrored in RTL. */
test('places a session by drag and drop on a valid cell', async ({ page, errors }) => {
  await login(page);
  await openDraft(page);
  const items = page.locator('.u-item');
  const before = await page.locator('.slot-item').count();

  const source = await items.first().boundingBox();
  if (!source) throw new Error('no unplaced item');
  await page.mouse.move(source.x + source.width / 2, source.y + source.height / 2);
  await page.mouse.down();
  await page.mouse.move(source.x + source.width / 2 - 30, source.y + source.height / 2 + 10, { steps: 5 });
  const target = await visibleValidCell(page);
  await page.mouse.move(target.x, target.y, { steps: 15 });
  await page.waitForTimeout(250);
  await page.mouse.up();

  await expect(page.locator('.slot-item')).toHaveCount(before + 1);
  expect(errors).toEqual([]);
});

test('keyboard placement: Enter on an unplaced session, arrows, Enter on a cell', async ({ page }) => {
  await login(page);
  await openDraft(page);
  const before = await page.locator('.slot-item').count();
  await page.locator('.u-item').first().focus();
  await page.keyboard.press('Enter');
  const valid = page.locator('.cell.valid, .cell.penalty').first();
  await expect(valid).toBeAttached();
  await valid.focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('.slot-item')).toHaveCount(before + 1);
});
