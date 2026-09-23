const productsUser = process.env.PRODUCTS_DB_USER;
const productsPassword = process.env.PRODUCTS_DB_PASSWORD;

if (!productsUser || !productsPassword) {
    throw new Error("PRODUCTS_DB_USER and PRODUCTS_DB_PASSWORD must be set");
}

db = db.getSiblingDB("products");
db.dropDatabase();

db = db.getSiblingDB("products");
const productsRole = [{ role: "readWrite", db: "products" }];
if (db.getUser(productsUser)) {
    db.updateUser(productsUser, {
        pwd: productsPassword,
        roles: productsRole
    });
} else {
    db.createUser({
        user: productsUser,
        pwd: productsPassword,
        roles: productsRole
    });
}

print("[reset] Recreated products database and provisioned application user '" + productsUser + "'");
