const { test, expect } = require('@playwright/test');
const { launchDesktop } = require('./support/desktop.cjs');

test('unknown set printings import with visible substitutions, while unknown cards remain blocked', async () => {
  const { application } = await launchDesktop('import-printings-ui');
  try {
    const page = await application.firstWindow();
    await expect(page.locator('#loading')).toBeHidden({ timeout: 60000 });
    await page.locator('#import-button').click();
    await page.locator('#import-name').fill('Imported dual lands');
    await page.locator('#import-text').fill('1 Bayou (SUM) 283\u00a0\n1 Savannah (SUM) 285\n1 Scrubland (SUM) 286');
    await page.locator('#preview-import').click();
    await expect(page.locator('#confirm-import')).toBeEnabled();
    await expect(page.locator('#import-preview')).toContainText('3 cards');
    await expect(page.locator('.import-printings')).toContainText('Using available printings');
    await expect(page.locator('.import-printings p')).toHaveCount(3);
    for (const name of ['Bayou', 'Savannah', 'Scrubland']) {
      await expect(page.locator('.import-printings')).toContainText(`${name}: SUM is not in the library; using`);
    }
    await page.locator('#confirm-import').click();
    await expect(page.locator('#import-dialog')).toBeHidden();
    await expect(page.locator('#deck-name')).toHaveValue('Imported dual lands');
    await expect(page.locator('#main-count')).toHaveText('3');
    for (const name of ['Bayou', 'Savannah', 'Scrubland']) {
      await expect(page.locator('#deck-list')).toContainText(name);
    }
    await page.locator('#import-button').click();
    await page.locator('#import-text').fill('1 Definitely Not A Real Card (SUM) 283');
    await page.locator('#preview-import').click();
    await expect(page.locator('#import-preview')).toContainText('Resolve these lines');
    await expect(page.locator('#confirm-import')).toBeDisabled();
    await expect(page.locator('.import-printings')).toHaveCount(0);
  } finally { await application.close(); }
});
