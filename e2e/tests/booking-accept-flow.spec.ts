import { test, expect, Page } from '@playwright/test';
import axios from 'axios';
import * as path from 'path';
import {
  ADMIN,
  CALENDAR_SERVICE_URL,
  PRODUCT_SERVICE_URL,
} from '../config';
import { deleteUserAsAdmin, loginAndGetToken, registerUser, writeStorageState } from '../helpers';

interface CreatedTaskMaster {
  id: string;
  ownerUsername: string;
  name: string;
}

interface BookingDto {
  id: string;
  taskMasterId: string;
  requesterUsername: string;
  slotStart: string;
  slotEnd: string;
  durationHours: number;
  status: 'PENDING' | 'ACCEPTED' | 'DECLINED' | 'CANCELLED';
}

// Per-run TaskMaster (no public UI exists to create one without going through
// the application + admin-approval flow, so this stays as a direct API setup).
async function createTaskMaster(ownerUsername: string): Promise<CreatedTaskMaster> {
  const res = await axios.post<CreatedTaskMaster>(
    `${PRODUCT_SERVICE_URL}/products`,
    {
      name: `E2E TM ${ownerUsername}`,
      age: 30,
      photo: '',
      location: 'Toronto, ON',
      rating: 4.5,
      jobCategories: ['e2e-ui-booking'],
      description: 'Created by booking-accept-flow e2e test (UI scenario).',
      hourlyRateUsd: 50,
      ownerUsername,
    },
    { validateStatus: () => true },
  );
  if (res.status >= 300) {
    throw new Error(`Failed to create TaskMaster: ${res.status} ${JSON.stringify(res.data)}`);
  }
  return res.data;
}

/**
 * Picks a target slot 8 days ahead at a fixed local hour, so the slot is safely
 * in the future and the day-of-week column is unambiguous in the calendar grid.
 */
function pickTargetSlot(): { date: Date; startHour: number; endHour: number } {
  const date = new Date();
  date.setDate(date.getDate() + 8);
  date.setHours(0, 0, 0, 0);
  return { date, startHour: 10, endHour: 11 }; // 2-hour selection (10:00 → 12:00 local)
}

/** Day-of-week index used by the Calendar (Sunday = 0, matching weekStartsOn: 0). */
function dayColumnIndex(date: Date): number {
  return date.getDay();
}

/** Mouse-drag across `.hour-cell` from startHour to endHour in the given day column. */
async function dragSelectSlot(page: Page, dayIndex: number, startHour: number, endHour: number): Promise<void> {
  const dayColumn = page.locator('.day-columns .day-column').nth(dayIndex);
  await expect(dayColumn, 'day column should be rendered').toBeVisible();

  const startCell = dayColumn.locator('.hour-cell').nth(startHour);
  const endCell = dayColumn.locator('.hour-cell').nth(endHour);

  await startCell.scrollIntoViewIfNeeded();

  const startBox = await startCell.boundingBox();
  const endBox = await endCell.boundingBox();
  if (!startBox || !endBox) {
    throw new Error('Could not measure calendar cells for drag');
  }

  const startX = startBox.x + startBox.width / 2;
  const startY = startBox.y + startBox.height / 2;
  const endX = endBox.x + endBox.width / 2;
  const endY = endBox.y + endBox.height / 2;

  await page.mouse.move(startX, startY);
  await page.mouse.down();
  // Intermediate moves ensure the mouseenter handler runs against in-between
  // cells (which is what flips movedRef and turns the click into a real drag).
  await page.mouse.move(startX, (startY + endY) / 2, { steps: 5 });
  await page.mouse.move(endX, endY, { steps: 5 });
  await page.mouse.up();

  await expect(page.locator('.selection')).toBeVisible();
}

/** Navigate the visible calendar week forward until it contains the target date. */
async function navigateCalendarToWeekContaining(page: Page, target: Date): Promise<void> {
  const targetDay = String(target.getDate());
  const nextWeek = page.getByRole('button', { name: /Next week/i });
  const targetHeader = page.locator('.day-header').nth(dayColumnIndex(target));

  for (let i = 0; i < 12; i++) {
    const text = (await targetHeader.locator('.day-date').textContent())?.trim();
    if (text === targetDay) return;
    await nextWeek.click();
  }
  throw new Error(`Calendar did not reach the week containing day ${targetDay}`);
}

test.describe('Booking request → accept → timetable becomes busy (UI-driven)', () => {
  const runId = Date.now().toString(36).toLowerCase();
  const booker = {
    username: `e2e_ui_bk_${runId}`,
    password: 'Test1234!',
    email: `e2e_ui_bk_${runId}@example.com`,
  };
  const owner = {
    username: `e2e_ui_ow_${runId}`,
    password: 'Test1234!',
    email: `e2e_ui_ow_${runId}@example.com`,
  };

  const bookerStateFile = path.join('.auth', `ui-booker-${runId}.json`);
  const ownerStateFile = path.join('.auth', `ui-owner-${runId}.json`);

  let taskMaster: CreatedTaskMaster;
  let bookerToken: string;
  let ownerToken: string;

  test.beforeAll(async () => {
    await registerUser(booker);
    await registerUser(owner);
    taskMaster = await createTaskMaster(owner.username);

    bookerToken = await loginAndGetToken(booker.username, booker.password);
    ownerToken = await loginAndGetToken(owner.username, owner.password);

    writeStorageState(bookerStateFile, bookerToken, booker.username);
    writeStorageState(ownerStateFile, ownerToken, owner.username);
  });

  test.afterAll(async () => {
    try {
      const adminToken = await loginAndGetToken(ADMIN.username, ADMIN.password);
      await deleteUserAsAdmin(adminToken, booker.username);
      await deleteUserAsAdmin(adminToken, owner.username);
    } catch (e) {
      console.warn('[teardown] failed:', (e as Error).message);
    }
  });

  test('booker submits via calendar drag, owner accepts in inbox, slot shown busy', async ({ browser }) => {
    const slot = pickTargetSlot();

    // ---------- BOOKER: open the TaskMaster page and click "Book Now" ----------
    const bookerContext = await browser.newContext({ storageState: bookerStateFile });
    const bookerPage = await bookerContext.newPage();
    // Auto-dismiss the success / error alert that submit raises.
    bookerPage.on('dialog', dialog => { void dialog.accept(); });

    await bookerPage.goto(`/product/${taskMaster.id}`);
    await expect(bookerPage.getByRole('heading', { name: new RegExp(taskMaster.name) })).toBeVisible();

    await bookerPage.getByRole('button', { name: /Book Now/i }).click();
    await expect(bookerPage).toHaveURL(new RegExp(`/booking/${taskMaster.id}$`));
    await expect(bookerPage.locator('.calendar')).toBeVisible();

    // ---------- BOOKER: navigate the calendar and drag to select 2 hours ----------
    await navigateCalendarToWeekContaining(bookerPage, slot.date);
    await dragSelectSlot(bookerPage, dayColumnIndex(slot.date), slot.startHour, slot.endHour);

    await expect(bookerPage.getByText(/Selected:/)).toBeVisible();
    await expect(bookerPage.locator('.selection-label')).toHaveText(/2 hours/);

    await bookerPage.locator('#booking-description').fill('e2e UI: please accept');

    await bookerPage.getByRole('button', { name: /^Submit$/ }).click();

    // Submit clears the selection on success — wait for that as visible confirmation.
    await expect(bookerPage.getByText(/Click a time slot/)).toBeVisible({ timeout: 15_000 });

    // The UI doesn't surface the new booking id, so look it up via the API.
    const outgoingRes = await axios.get<BookingDto[]>(
      `${CALENDAR_SERVICE_URL}/api/booking/outgoing`,
      { headers: { Authorization: `Bearer ${bookerToken}` }, validateStatus: () => true },
    );
    expect(outgoingRes.status).toBe(200);
    const created = outgoingRes.data.find(
      b => b.taskMasterId === taskMaster.id && b.status === 'PENDING',
    );
    expect(created, 'expected the new PENDING booking to be visible on the outgoing list').toBeDefined();
    const bookingId = created!.id;

    // ---------- OWNER: open incoming inbox via the UI and accept ----------
    const ownerContext = await browser.newContext({ storageState: ownerStateFile });
    const ownerPage = await ownerContext.newPage();
    ownerPage.on('dialog', dialog => { void dialog.accept(); });

    await ownerPage.goto('/bookings/incoming');
    await expect(ownerPage.getByRole('heading', { name: /Booking Requests/i })).toBeVisible();

    // PENDING tab is selected by default. Find the request card for our booker.
    // Pick the innermost div that contains BOTH the booker's name and an Accept button.
    const requestCard = ownerPage
      .locator('.new-taskmaster-page div')
      .filter({ has: ownerPage.locator(`strong:has-text("${booker.username}")`) })
      .filter({ has: ownerPage.getByRole('button', { name: /^Accept$/ }) })
      .last();
    await expect(requestCard, 'owner should see the booker\'s request').toBeVisible({ timeout: 20_000 });

    await requestCard.getByRole('button', { name: /^Accept$/ }).click();

    // The card's status badge flips from PENDING to ACCEPTED in place (the list
    // doesn't auto re-filter). After the click the Accept button is gone, so
    // re-locate the card by both the booker's name and the status badge.
    const acceptedCard = ownerPage
      .locator('.new-taskmaster-page div')
      .filter({ has: ownerPage.locator(`strong:has-text("${booker.username}")`) })
      .filter({ has: ownerPage.locator('.status-badge-sm') })
      .last();
    await expect(acceptedCard.locator('.status-badge-sm')).toHaveText('ACCEPTED', { timeout: 20_000 });
    await expect(acceptedCard.getByRole('button', { name: /^Accept$/ })).toHaveCount(0);

    // ---------- OWNER: notification bell records the submitted request ----------
    await ownerPage.goto('/');
    const ownerBell = ownerPage.locator('a', { hasText: /Notifications/ }).first();
    await expect(ownerBell).toBeVisible();
    await expect
      .poll(async () => {
        await ownerBell.click();
        const popup = ownerPage.locator('.notification-popup');
        await expect(popup).toBeVisible({ timeout: 5_000 });
        const text = (await popup.textContent()) ?? '';
        await ownerPage.keyboard.press('Escape');
        return text;
      }, { timeout: 30_000, intervals: [1000, 2000, 3000] })
      .toMatch(new RegExp(`booking request submitted|${booker.username}`, 'i'));

    // ---------- BOOKER: notification bell records the acceptance ----------
    await bookerPage.goto('/');
    const bookerBell = bookerPage.locator('a', { hasText: /Notifications/ }).first();
    await expect(bookerBell).toBeVisible();
    await expect
      .poll(async () => {
        await bookerBell.click();
        const popup = bookerPage.locator('.notification-popup');
        await expect(popup).toBeVisible({ timeout: 5_000 });
        const text = (await popup.textContent()) ?? '';
        await bookerPage.keyboard.press('Escape');
        return text;
      }, { timeout: 30_000, intervals: [1000, 2000, 3000] })
      .toMatch(/booking request accepted|accepted/i);

    // ---------- BOOKER: revisit the booking page; chosen cells are busy ----------
    await bookerPage.goto(`/booking/${taskMaster.id}`);
    await expect(bookerPage.locator('.calendar')).toBeVisible();
    await navigateCalendarToWeekContaining(bookerPage, slot.date);

    const targetColumn = bookerPage.locator('.day-columns .day-column').nth(dayColumnIndex(slot.date));
    await expect
      .poll(
        async () => targetColumn.locator('.hour-cell.busy').count(),
        { timeout: 15_000, intervals: [500, 1000, 2000] },
      )
      .toBeGreaterThanOrEqual(2);

    await expect(targetColumn.locator('.hour-cell').nth(slot.startHour)).toHaveClass(/busy/);
    await expect(targetColumn.locator('.hour-cell').nth(slot.endHour)).toHaveClass(/busy/);

    // Backend sanity check — the booking is genuinely ACCEPTED.
    const finalRes = await axios.get<BookingDto>(
      `${CALENDAR_SERVICE_URL}/api/booking/${bookingId}`,
      { validateStatus: () => true },
    );
    expect(finalRes.status).toBe(200);
    expect(finalRes.data.status).toBe('ACCEPTED');

    await bookerContext.close();
    await ownerContext.close();
  });
});
