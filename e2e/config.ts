export const FRONTEND_URL = process.env.FRONTEND_URL ?? 'http://localhost:3000';
export const AUTH_SERVICE_URL = process.env.AUTH_SERVICE_URL ?? 'http://localhost:8081';
export const PRODUCT_SERVICE_URL = process.env.PRODUCT_SERVICE_URL ?? 'http://localhost:8080';
export const NOTIFICATION_SERVICE_URL = process.env.NOTIFICATION_SERVICE_URL ?? 'http://localhost:8084';

export const ADMIN = {
  username: 'admin',
  password: 'admin',
};

export const AUTH_STATE_DIR = '.auth';
export const ADMIN_STATE_FILE = `${AUTH_STATE_DIR}/admin.json`;
export const USER_STATE_FILE = `${AUTH_STATE_DIR}/user.json`;
export const TEST_USER_FILE = `${AUTH_STATE_DIR}/test-user.json`;
