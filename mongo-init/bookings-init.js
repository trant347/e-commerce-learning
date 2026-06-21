const u = process.env.BOOKINGS_DB_USER;
const p = process.env.BOOKINGS_DB_PASSWORD;
if (!u || !p) { throw new Error("BOOKINGS_DB_USER and BOOKINGS_DB_PASSWORD must be set"); }
db = db.getSiblingDB("BookingsDB");
db.createUser({ user: u, pwd: p, roles: [{ role: "readWrite", db: "BookingsDB" }] });
print("[init] Created user '" + u + "' on db 'BookingsDB'");
