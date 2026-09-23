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

Then rebuild and restart product-service:

```powershell
docker compose up -d --build product-service
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
