import { test, expect, Page } from '@playwright/test';
import { ADMIN_STATE_FILE, USER_STATE_FILE, TEST_USER_FILE } from '../config';
import { readTestUser } from '../helpers';

const testUser = readTestUser(TEST_USER_FILE);

const applicationData = {
  name: 'Jane Doe',
  age: '32',
  location: 'Toronto, ON',
  description: 'Experienced tutor with 8 years of math instruction.',
  hourlyRateUsd: '49.99',
  category: 'tutoring',
};

async function fillAndSubmitApplication(page: Page): Promise<void> {
  await page.goto('/apply');

  await page.locator('input[name="name"]').fill(applicationData.name);
  await page.locator('input[name="age"]').fill(applicationData.age);
  await page.locator('input[name="location"]').fill(applicationData.location);
  await page.locator('textarea[name="description"]').fill(applicationData.description);
  await page.locator('input[name="hourlyRateUsd"]').fill(applicationData.hourlyRateUsd);

  const categoryInput = page.locator('input[placeholder="e.g. tutoring"]');
  await categoryInput.fill(applicationData.category);
  await categoryInput.press('Enter');
  await expect(page.locator('.category-tag', { hasText: applicationData.category })).toBeVisible();

  await page.getByRole('button', { name: /Submit Application/i }).click();
  await expect(page.getByText(/Application submitted!/i)).toBeVisible();
}

test.describe('TaskMaster application flow', () => {
  test('user submits application, admin reviews and accepts', async ({ browser }) => {
    // ---------- USER: submit application ----------
    const userContext = await browser.newContext({ storageState: USER_STATE_FILE });
    const userPage = await userContext.newPage();
    await fillAndSubmitApplication(userPage);

    // ---------- ADMIN: see and accept ----------
    const adminContext = await browser.newContext({ storageState: ADMIN_STATE_FILE });
    const adminPage = await adminContext.newPage();

    await adminPage.goto('/admin/applications');

    const applicationRow = adminPage.locator('tr, .application-row, li', {
      hasText: testUser.username,
    }).first();
    await expect(applicationRow).toBeVisible({ timeout: 20_000 });

    const reviewButton = applicationRow.getByRole('button', { name: /Review/i });
    if (await reviewButton.count()) {
      await reviewButton.click();
    } else {
      await applicationRow.click();
    }

    await expect(adminPage.getByText(/Application Review/i)).toBeVisible();
    await expect(adminPage.getByText(testUser.username)).toBeVisible();

    await adminPage.getByRole('button', { name: /Accept/i }).click();

    await expect(adminPage.locator('.status-badge', { hasText: 'ACCEPTED' })).toBeVisible({ timeout: 20_000 });
    await expect(adminPage.getByText(/Application accepted/i)).toBeVisible();

    // ---------- USER: receives acceptance notification ----------
    await userPage.goto('/');

    await expect
      .poll(async () => {
        const res = await userPage.request.get(`/api/notification/${testUser.username}`);
        if (!res.ok()) return [];
        const body = await res.json().catch(() => []);
        return Array.isArray(body) ? body : [];
      }, { timeout: 60_000, intervals: [1000, 2000, 3000] })
      .toEqual(
        expect.arrayContaining([
          expect.objectContaining({ type: expect.stringMatching(/ACCEPTED/i) }),
        ]),
      );

    await userContext.close();
    await adminContext.close();
  });
});
