import { ADMIN, ADMIN_STATE_FILE, USER_STATE_FILE, TEST_USER_FILE } from './config';
import {
  waitForAllServices,
  registerUser,
  loginAndGetToken,
  writeStorageState,
  writeTestUser,
  TestUser,
} from './helpers';

export default async function globalSetup(): Promise<void> {
  console.log('[global-setup] waiting for services...');
  await waitForAllServices();

  // Unique per-run user so repeated runs don't collide with prior PENDING/ACCEPTED state.
  const suffix = Date.now().toString(36);
  const testUser: TestUser = {
    username: `e2euser_${suffix}`,
    password: 'Test1234!',
    email: `e2euser_${suffix}@example.com`,
  };

  await registerUser(testUser);
  writeTestUser(TEST_USER_FILE, testUser);

  const userToken = await loginAndGetToken(testUser.username, testUser.password);
  writeStorageState(USER_STATE_FILE, userToken, testUser.username);

  const adminToken = await loginAndGetToken(ADMIN.username, ADMIN.password);
  writeStorageState(ADMIN_STATE_FILE, adminToken, ADMIN.username);

  console.log(`[global-setup] seeded user=${testUser.username}, admin ready.`);
}
