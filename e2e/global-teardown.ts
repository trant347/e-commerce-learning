import * as fs from 'fs';
import { ADMIN, TEST_USER_FILE } from './config';
import { deleteUserAsAdmin, loginAndGetToken, readTestUser } from './helpers';

export default async function globalTeardown(): Promise<void> {
  if (!fs.existsSync(TEST_USER_FILE)) {
    console.log('[global-teardown] no test-user file found; nothing to clean up.');
    return;
  }

  const testUser = readTestUser(TEST_USER_FILE);

  let adminToken: string;
  try {
    adminToken = await loginAndGetToken(ADMIN.username, ADMIN.password);
  } catch (e) {
    console.warn(`[global-teardown] could not authenticate admin: ${(e as Error).message}`);
    return;
  }

  await deleteUserAsAdmin(adminToken, testUser.username);

  try {
    fs.unlinkSync(TEST_USER_FILE);
  } catch {
    /* ignore */
  }
}
