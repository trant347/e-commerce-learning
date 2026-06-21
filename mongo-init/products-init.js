const u = process.env.PRODUCTS_DB_USER;
const p = process.env.PRODUCTS_DB_PASSWORD;
if (!u || !p) { throw new Error("PRODUCTS_DB_USER and PRODUCTS_DB_PASSWORD must be set"); }
db = db.getSiblingDB("products");
db.createUser({ user: u, pwd: p, roles: [{ role: "readWrite", db: "products" }] });
print("[init] Created user '" + u + "' on db 'products'");
