import axios, { AxiosError } from 'axios';
import * as fs from 'fs';
import * as path from 'path';
import { FRONTEND_URL, AUTH_SERVICE_URL, PRODUCT_SERVICE_URL, CALENDAR_SERVICE_URL, NOTIFICATION_SERVICE_URL } from './config';

const USER_NAME_KEY = 'USER_NAME_KEY_BOOKSTORE';

export interface TestUser {
  username: string;
  password: string;
  email: string;
}

export async function waitForUrl(url: string, label: string, timeoutMs = 180_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastErr: unknown;
  while (Date.now() < deadline) {
    try {
      const res = await axios.get(url, { timeout: 3000, validateStatus: () => true });
      if (res.status < 500) {
        console.log(`[ready] ${label} (${res.status})`);
        return;
      }
      lastErr = `status ${res.status}`;
    } catch (e) {
      lastErr = (e as Error).message;
    }
    await new Promise(r => setTimeout(r, 2000));
  }
  throw new Error(`Timed out waiting for ${label} at ${url}: ${lastErr}`);
}

export async function waitForAllServices(): Promise<void> {
  await Promise.all([
    waitForUrl(`${FRONTEND_URL}/`, 'frontend'),
    waitForUrl(`${AUTH_SERVICE_URL}/users`, 'authentication-service'),
    waitForUrl(`${PRODUCT_SERVICE_URL}/`, 'product-service'),
    waitForUrl(`${CALENDAR_SERVICE_URL}/health`, 'calendar-service'),
    waitForUrl(`${NOTIFICATION_SERVICE_URL}/health`, 'notification-service'),
  ]);
}

export async function registerUser(user: TestUser): Promise<void> {
  try {
    await axios.post(`${AUTH_SERVICE_URL}/register`, {
      username: user.username,
      password: user.password,
      email: user.email,
      firstName: 'Test',
      lastName: 'User',
      role: 'user',
    });
    console.log(`[seed] registered user ${user.username}`);
  } catch (e) {
    const err = e as AxiosError;
    // Allow re-runs: ignore "already exists" style errors
    if (err.response && err.response.status >= 400 && err.response.status < 500) {
      console.log(`[seed] user ${user.username} may already exist (${err.response.status})`);
      return;
    }
    throw e;
  }
}

export async function loginAndGetToken(username: string, password: string): Promise<string> {
  const res = await axios.post(
    `${AUTH_SERVICE_URL}/authenticate`,
    { username, password },
    { headers: { 'Content-Type': 'application/json' } },
  );
  // Auth-service returns the JWT as a raw string body.
  return typeof res.data === 'string' ? res.data : String(res.data);
}

export async function deleteUserAsAdmin(adminToken: string, username: string): Promise<void> {
  try {
    const res = await axios.delete(`${AUTH_SERVICE_URL}/${encodeURIComponent(username)}`, {
      headers: { Authorization: `Bearer ${adminToken}` },
      validateStatus: () => true,
    });
    if (res.status === 204 || res.status === 404) {
      console.log(`[teardown] deleted user ${username} (${res.status})`);
      return;
    }
    console.warn(`[teardown] unexpected status deleting ${username}: ${res.status} ${JSON.stringify(res.data)}`);
  } catch (e) {
    console.warn(`[teardown] failed to delete user ${username}: ${(e as Error).message}`);
  }
}

export function writeStorageState(filePath: string, token: string, username: string): void {
  const dir = path.dirname(filePath);
  if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });

  const state = {
    cookies: [],
    origins: [
      {
        origin: FRONTEND_URL,
        localStorage: [
          { name: 'token', value: token },
          { name: USER_NAME_KEY, value: username },
        ],
      },
    ],
  };
  fs.writeFileSync(filePath, JSON.stringify(state, null, 2));
}

export function readTestUser(filePath: string): TestUser {
  return JSON.parse(fs.readFileSync(filePath, 'utf-8')) as TestUser;
}

export function writeTestUser(filePath: string, user: TestUser): void {
  const dir = path.dirname(filePath);
  if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(filePath, JSON.stringify(user, null, 2));
}
