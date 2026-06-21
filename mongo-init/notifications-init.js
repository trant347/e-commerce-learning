const u = process.env.NOTIFICATIONS_DB_USER;
const p = process.env.NOTIFICATIONS_DB_PASSWORD;
if (!u || !p) { throw new Error("NOTIFICATIONS_DB_USER and NOTIFICATIONS_DB_PASSWORD must be set"); }
db = db.getSiblingDB("NotificationDB");
db.createUser({ user: u, pwd: p, roles: [{ role: "readWrite", db: "NotificationDB" }] });
print("[init] Created user '" + u + "' on db 'NotificationDB'");
