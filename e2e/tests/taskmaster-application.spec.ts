import { test, expect, Page } from '@playwright/test';
import { ADMIN_STATE_FILE, USER_STATE_FILE, TEST_USER_FILE } from '../config';
import { readTestUser } from '../helpers';

async function getBearerToken(page: Page): Promise<string> {
  return page.evaluate(() => localStorage.getItem('token') ?? '');
}

async function fillAndSubmitApplication(
  page: Page,
  categoryDisplayName: string,
): Promise<void> {
  await page.goto('/apply');

  await page.locator('input[name="name"]').fill('Jane Doe');
  await page.locator('input[name="age"]').fill('32');
  await page.locator('input[name="city"]').fill('Chicago');
  await page.locator('select[name="stateCode"]').selectOption('IL');
  await page.locator('textarea[name="description"]')
    .fill('Experienced specialist for the new catalog service.');
  await page.locator('input[name="hourlyRateUsd"]').fill('49.99');
  await page.getByRole('checkbox', { name: new RegExp(categoryDisplayName, 'i') }).check();

  await page.getByRole('button', { name: /Submit Application/i }).click();
  await expect(page.getByText(/Application submitted!/i)).toBeVisible();
}

test.describe('TaskMaster application flow', () => {
  test('admin category remains canonical through application approval', async ({ browser }) => {
    const testUser = readTestUser(TEST_USER_FILE);
    const categorySuffix = Date.now().toString(36);
    const categoryId = `e2e-service-${categorySuffix}`;
    const categoryDisplayName = `E2E Service ${categorySuffix}`;

    // ---------- ADMIN: create the canonical category ----------
    const adminContext = await browser.newContext({ storageState: ADMIN_STATE_FILE });
    const adminPage = await adminContext.newPage();
    await adminPage.goto('/admin/categories');
    await adminPage.getByLabel(/Canonical ID/i).fill(categoryId);
    await adminPage.getByLabel(/^Display Name/i).fill(categoryDisplayName);
    await adminPage.getByLabel(/^Description/i)
      .fill('Category created by the TaskMaster application regression scenario.');
    await adminPage.getByRole('button', { name: /Create Category/i }).click();
    await expect(adminPage.getByText('Category created.')).toBeVisible();
    await expect(adminPage.getByText(categoryId, { exact: true })).toBeVisible();

    // ---------- USER: arbitrary API categories are rejected ----------
    const userContext = await browser.newContext({ storageState: USER_STATE_FILE });
    const userPage = await userContext.newPage();
    await userPage.goto('/');
    const userToken = await getBearerToken(userPage);
    const validApplication = {
      name: 'Jane Doe',
      age: 32,
      location: 'Chicago, IL',
      description: 'Invalid category API regression probe.',
      hourlyRateUsd: 49.99,
      photo: null,
    };
    const unknownResponse = await userPage.request.post('/products/applications', {
      headers: { Authorization: `Bearer ${userToken}` },
      data: {
        ...validApplication,
        jobCategories: [`arbitrary-${categorySuffix}`],
      },
    });
    expect(unknownResponse.status()).toBe(400);
    await expect(unknownResponse.json()).resolves.toMatchObject({
      error: 'unknown_category',
    });

    const emptyResponse = await userPage.request.post('/products/applications', {
      headers: { Authorization: `Bearer ${userToken}` },
      data: {
        ...validApplication,
        jobCategories: [],
      },
    });
    expect(emptyResponse.status()).toBe(400);
    await expect(emptyResponse.json()).resolves.toMatchObject({
      error: 'invalid_category',
    });

    // ---------- USER: select the catalog category and submit ----------
    await fillAndSubmitApplication(userPage, categoryDisplayName);

    // ---------- ADMIN: see and accept ----------
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

    // ---------- USER: resulting profile retains the canonical ID ----------
    await expect.poll(async () => {
      const response = await userPage.request.get('/products/me/taskmaster', {
        headers: { Authorization: `Bearer ${userToken}` },
      });
      if (!response.ok()) return [];
      const profile = await response.json();
      return Array.isArray(profile.jobCategories) ? profile.jobCategories : [];
    }, { timeout: 30_000, intervals: [500, 1000, 2000] })
      .toContain(categoryId);

    await expect.poll(async () => {
      const response = await userPage.request.get(
        `/api/notification/${testUser.username}`,
      );
      if (!response.ok()) return [];
      const notifications = await response.json().catch(() => []);
      return Array.isArray(notifications) ? notifications : [];
    }, { timeout: 60_000, intervals: [1000, 2000, 3000] })
      .toEqual(expect.arrayContaining([
        expect.objectContaining({ type: expect.stringMatching(/ACCEPTED/i) }),
      ]));

    await userContext.close();
    await adminContext.close();
  });
});
