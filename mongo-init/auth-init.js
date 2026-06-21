// Runs once on first start of mongo-auth, after the root user has been created.
// Creates an application user with readWrite *only* on the `user` database.
const u = process.env.AUTH_DB_USER;
const p = process.env.AUTH_DB_PASSWORD;
if (!u || !p) { throw new Error("AUTH_DB_USER and AUTH_DB_PASSWORD must be set"); }
db = db.getSiblingDB("user");
db.createUser({ user: u, pwd: p, roles: [{ role: "readWrite", db: "user" }] });
print("[init] Created user '" + u + "' on db 'user'");
