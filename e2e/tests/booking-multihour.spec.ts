import { test, expect } from '@playwright/test';
import axios from 'axios';
import { ADMIN, AUTH_SERVICE_URL, CALENDAR_SERVICE_URL, PRODUCT_SERVICE_URL } from '../config';
import { loginAndGetToken, registerUser, deleteUserAsAdmin } from '../helpers';

interface CreatedTaskMaster {
  id: string;
  ownerUsername: string;
  name: string;
}

async function createTaskMaster(ownerUsername: string): Promise<CreatedTaskMaster> {
  const payload = {
    name: `E2E TaskMaster ${ownerUsername}`,
    age: 30,
    photo: '',
    location: 'Toronto, ON',
    rating: 4.5,
    jobCategories: ['e2e-booking'],
    description: 'Created by booking-multihour e2e test',
    hourlyRateUsd: 50,
    ownerUsername,
  };
  // POST /products is open to anonymous (JwtTokenFilter only guards "/products/").
  const res = await axios.post<CreatedTaskMaster>(`${PRODUCT_SERVICE_URL}/products`, payload, {
    validateStatus: () => true,
  });
  if (res.status >= 300) {
    throw new Error(`Failed to create TaskMaster: ${res.status} ${JSON.stringify(res.data)}`);
  }
  return res.data;
}

function pickFutureSlotStart(): Date {
  const base = new Date();
  base.setUTCDate(base.getUTCDate() + 30);
  base.setUTCHours(9 + Math.floor(Math.random() * 6), 0, 0, 0);
  return base;
}

function addHours(d: Date, h: number): Date {
  return new Date(d.getTime() + h * 3_600_000);
}

test.describe('Multi-hour booking with overlap enforcement', () => {
  // Per-run usernames so reruns and parallel shards never collide. Lowercased to
  // match calendar-service's NormalizeUsername (avoids surprises in assertions).
  const runId = Date.now().toString(36).toLowerCase();
  const booker = {
    username: `e2e_booker_${runId}`,
    password: 'Test1234!',
    email: `e2e_booker_${runId}@example.com`,
  };
  const owner = {
    username: `e2e_owner_${runId}`,
    password: 'Test1234!',
    email: `e2e_owner_${runId}@example.com`,
  };
  let taskMaster: CreatedTaskMaster;
  let bookerAuth: { Authorization: string };

  test.beforeAll(async () => {
    await registerUser(booker);
    await registerUser(owner);
    taskMaster = await createTaskMaster(owner.username);

    const token = await loginAndGetToken(booker.username, booker.password);
    bookerAuth = { Authorization: `Bearer ${token}` };
  });

  test.afterAll(async () => {
    // Admin-driven delete publishes USER_DELETED, which cascades to
    // product-service (drops the TaskMaster) and calendar-service (drops bookings).
    try {
      const adminToken = await loginAndGetToken(ADMIN.username, ADMIN.password);
      await deleteUserAsAdmin(adminToken, booker.username);
      await deleteUserAsAdmin(adminToken, owner.username);
    } catch (e) {
      console.warn('[teardown] failed:', (e as Error).message);
    }
  });

  test('books multi-hour slot; overlapping requests rejected; adjacent slot succeeds', async () => {
    const firstStart = pickFutureSlotStart();
    const firstDuration = 3; // e.g. 09:00 → 12:00 UTC

    // 1) First multi-hour booking succeeds (PENDING).
    const createFirst = await axios.post(
      `${CALENDAR_SERVICE_URL}/api/booking`,
      {
        taskMasterId: taskMaster.id,
        slotStart: firstStart.toISOString(),
        durationHours: firstDuration,
        message: 'e2e: first multi-hour booking',
      },
      { headers: bookerAuth, validateStatus: () => true },
    );
    expect(createFirst.status, JSON.stringify(createFirst.data)).toBe(200);
    expect(createFirst.data).toMatchObject({
      taskMasterId: taskMaster.id,
      durationHours: firstDuration,
    });

    // 2) Exact-same start, same duration → overlap → 409 Conflict.
    const overlapExact = await axios.post(
      `${CALENDAR_SERVICE_URL}/api/booking`,
      { taskMasterId: taskMaster.id, slotStart: firstStart.toISOString(), durationHours: firstDuration },
      { headers: bookerAuth, validateStatus: () => true },
    );
    expect(overlapExact.status).toBe(409);

    // 3) Starts 1h into the first booking → partial overlap → 409.
    const overlapPartial = await axios.post(
      `${CALENDAR_SERVICE_URL}/api/booking`,
      { taskMasterId: taskMaster.id, slotStart: addHours(firstStart, 1).toISOString(), durationHours: 2 },
      { headers: bookerAuth, validateStatus: () => true },
    );
    expect(overlapPartial.status).toBe(409);

    // 4) Starts 1h BEFORE the first booking and runs 2h → tail overlaps → 409.
    const overlapTail = await axios.post(
      `${CALENDAR_SERVICE_URL}/api/booking`,
      { taskMasterId: taskMaster.id, slotStart: addHours(firstStart, -1).toISOString(), durationHours: 2 },
      { headers: bookerAuth, validateStatus: () => true },
    );
    expect(overlapTail.status).toBe(409);

    // 5) Adjacent slot starting exactly when the first ends → no overlap → 200.
    const adjacent = await axios.post(
      `${CALENDAR_SERVICE_URL}/api/booking`,
      {
        taskMasterId: taskMaster.id,
        slotStart: addHours(firstStart, firstDuration).toISOString(),
        durationHours: 1,
      },
      { headers: bookerAuth, validateStatus: () => true },
    );
    expect(adjacent.status, JSON.stringify(adjacent.data)).toBe(200);
  });

  test('rejects durationHours outside the 1..24 range', async () => {
    const slotStart = pickFutureSlotStart().toISOString();

    for (const badDuration of [0, 25]) {
      const res = await axios.post(
        `${CALENDAR_SERVICE_URL}/api/booking`,
        { taskMasterId: taskMaster.id, slotStart, durationHours: badDuration },
        { headers: bookerAuth, validateStatus: () => true },
      );
      expect([400, 409], `durationHours=${badDuration} should be rejected`).toContain(res.status);
    }
  });
});
