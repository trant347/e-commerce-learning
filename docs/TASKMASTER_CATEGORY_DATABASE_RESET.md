# TaskMaster Category Database Reset

The category-catalog rollout uses a clean reset of the `products` MongoDB
database instead of an in-place legacy migration. This is appropriate only
while existing product data is disposable.

The reset deletes:

- TaskMaster profiles in `taskmaster`.
- Applications in `taskmaster_applications`, including pending and historical
  accepted/declined records.
- The category catalog in `categories`.
- Any other collections stored in the `products` logical database.

It does not delete the authorization, booking, notification, or payment
databases.

## Known side effects

TaskMaster IDs are regenerated when mock profiles are seeded. Existing booking
or escrow records in other service databases can therefore reference
TaskMasters that no longer exist. Files in the `product-static` Docker volume
are not deleted and may become orphaned.

For a disposable local environment with cross-service test data, prefer a full
stack data reset instead of retaining those references:

```powershell
docker compose down -v
docker compose up -d --build
```

This broader command deletes all Compose-managed database and broker volumes,
not only product data.

## Before resetting

1. Stop writes to product-service.
2. Confirm that TaskMaster profiles and application history can be discarded.
3. If the data might be needed later, take a MongoDB backup before continuing.
   This rollout intentionally provides no automatic conversion or rollback of
   legacy category strings.

## Reset

Run from the repository root while the Compose `mongo` service is running:

```powershell
.\scripts\reset-products-db.ps1 -ConfirmReset
```

The script stops product-service, authenticates as the configured MongoDB root
user, drops only the `products` logical database, recreates its least-privilege
application user, and removes `products:*` Redis cache entries when Redis is
running. It does not contain or print database passwords.

Then rebuild the product-service image and restart it:

```powershell
Push-Location product-service; .\build.bat; Pop-Location
docker compose up -d product-service
```

On startup, product-service:

1. Loads the version-controlled catalog from
   `product-service/src/main/resources/seed/categories.json`.
2. Creates only missing canonical categories through `CategoryService`.
3. Loads `seed/taskMasters.json` only when the TaskMaster collection is empty.
4. Validates every mock TaskMaster category against the seeded catalog before
   saving the profile.

Startup fails rather than silently converting data when TaskMaster profiles or
applications exist but the category catalog is absent. This protects against
accidentally deploying the reset-only rollout over a legacy database.

## Seed maintenance

- Add canonical bootstrap categories in `seed/categories.json`.
- Mock TaskMasters may reference only IDs from that file.
- Seed category IDs follow the same normalization and uniqueness rules as
  administrator-created categories.
- Startup is repeatable: existing category IDs are preserved, missing seed
  entries are created, and existing TaskMaster data is not regenerated.
- Editing metadata for an existing category requires the admin API or another
  clean reset; startup does not overwrite administrator-managed metadata.
- After administrators begin managing production-like data, replace this
  reset-only process with an explicit migration before preserving legacy data.

## Coordinated release

Backend catalog enforcement and the selection-only forms ship together. Do not
deploy the product-service or frontend changes separately: an old frontend
sends free-text categories that the new backend rejects, and a new frontend
depends on `GET /products/categories/metadata`, which an old backend lacks.

1. **Approve the initial catalog.** Review
   `product-service/src/main/resources/seed/categories.json` and confirm that
   every mock TaskMaster in `seed/taskMasters.json` references only those IDs.
   `ProductDataSeederTest` fails the build when a seed ID is duplicated or a
   mock profile references an unknown category.
2. **Freeze product writes.** Stop accepting applications and profile edits
   until the release finishes.
3. **Reset the products database** with the reset procedure above. Legacy
   profiles, applications, and categories are not retained under this rollout.
4. **Build and deploy product-service and frontend together.** Compose uses
   prebuilt `product-service` and `frontend` images, so rebuild both images
   before restarting them; `docker compose up --build` does not rebuild them.

   ```powershell
   Push-Location product-service; .\build.bat; Pop-Location
   Push-Location frontend; .\build.cmd; Pop-Location
   docker compose up -d product-service frontend
   ```

5. **Restart the AI assistant** so its MCP discovery reloads the catalog-backed
   category list:

   ```powershell
   docker compose restart ai-assistant-service
   ```

6. **Verify the release:**
   - `GET http://localhost:3000/products/categories` returns the seeded
     canonical IDs.
   - `GET http://localhost:3000/products/categories/metadata` returns display
     names and descriptions.
   - The `/apply` and admin **New TaskMaster** forms show catalog checkboxes
     and no free-text category input.
   - Submitting an unknown category directly to `POST /products/applications`
     returns `400` with `unknown_category`.

### Category cache during deployment

The catalog cache uses the versioned key `products:categories:v2`, so entries
from the earlier profile-derived cache (`products:categories`) are never read.
Every product-service startup also invalidates the catalog cache after seeding.
It bumps `products:categories:version` and deletes the cached ID list, so a
catalog cached by the previous release or before a reset cannot outlive the
deployment, even when Redis was unavailable to the reset script. Seeding mock
TaskMasters also evicts the list and filter caches.

To clear the catalog cache manually while product-service is running:

```powershell
docker compose exec -T redis redis-cli del products:categories:v2
```

If the cache key format changes in a future release, bump the key suffix in
`ProductCacheService.CATEGORIES_KEY` rather than relying on TTL expiry.

## Managing categories as an administrator

Only users whose JWT carries `ROLE_ADMIN` can create or edit categories; the
backend enforces this. The admin UI is currently shown only to the `admin`
account. Other `ROLE_ADMIN` users must use the API.

### From the UI

1. Sign in as `admin` and choose **Categories** in the header menu,
   or open `http://localhost:3000/admin/categories`.
2. Under **Create Category**, enter:
   - **Canonical ID**: a permanent lowercase slug of 2-64 characters using
     letters, digits, and single hyphens, for example `appliance-repair`.
     IDs cannot be changed after creation.
   - **Display Name**: 2-80 characters shown to applicants and customers.
     Display names must be unique ignoring case and repeated spaces.
   - **Description**: up to 500 characters describing the included work.
3. Choose **Create Category**. The category is available in application and
   profile forms right away, even before any TaskMaster offers it.
4. To change a category's display name or description, choose **Edit** in its
   row. Profiles and applications keep referencing the unchanged ID.

### From the API

```http
POST /products/admin/categories
Authorization: Bearer <admin JWT>
Content-Type: application/json

{ "id": "appliance-repair", "displayName": "Appliance Repair",
  "description": "Household appliance diagnosis and repair." }
```

`PUT /products/admin/categories/{id}` accepts `displayName` and `description`
only. Responses: `201`/`200` on success, `400 invalid_category` for malformed
fields, `409` for duplicate IDs or display names, `401` without a trusted
identity, and `403` for non-administrators.

### Operational notes

- Deleting, merging, and retiring categories are not supported in this phase.
  Choose IDs carefully: applicants' and profiles' stored categories reference
  them permanently.
- Adding a category to `seed/categories.json` only affects fresh or reset
  environments and environments missing that ID. Use the admin UI or API for
  live catalogs.
- The AI assistant reads the category list for its search tool at startup.
  Restart `ai-assistant-service` after creating categories so AI search can
  select them. REST search and forms see new categories immediately.
