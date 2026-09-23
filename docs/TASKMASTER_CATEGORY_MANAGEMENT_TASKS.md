# TaskMaster Category Management Tasks

Status: Tasks 1-5 complete; implementation tasks 6-11 have not started.

Source: [TaskMaster Category Resolution Specification](TASKMASTER_CATEGORY_RESOLUTION_SPEC.md).

## Scope and sequencing

Implement the admin-owned category catalog first. TaskMaster applicants select
existing categories rather than creating marketplace categories through free
text. Store the catalog in the product-service MongoDB database from the start.

This plan intentionally changes the source specification's rollout order:

1. Admin-managed category catalog and selection-only forms.
2. User requests for missing categories and admin resolution.
3. AI multi-category search and, separately, deterministic category resolution.

Phase 1 does not change AI prompts, search-tool arguments, ranking, category
limits, or natural-language resolution. The current `search_task_masters` tool
accepts one category; the specification proposes up to three, not an existing
two-category limit. That expansion remains deferred.

## Finalized data-model contract

Use a dedicated `Category` model and `categories` collection, not a new
singular field on each TaskMaster.

Keep `TaskMaster.jobCategories` and application `jobCategories` as arrays of
canonical category IDs, as specified. This preserves existing profile contracts
and allows providers to offer more than one service without changing AI search.

Initial category fields are immutable canonical `id`, `displayName`,
server-managed `normalizedDisplayName`, `description`, and server-managed
creation/update timestamps and actor identity. IDs use lowercase hyphenated
ASCII slugs; both IDs and normalized display names are unique. The complete
normalization, validation, API, and error contract is recorded in the source
specification.

Aliases, object terms, related categories, merging, retirement, and required
profile attributes are deferred. Do not add hard deletion in the initial phase:
removing referenced categories requires an explicit reassignment policy.

## Current implementation

- `ProductCacheService.getCategories()` derives categories from existing
  TaskMaster profiles rather than an independent catalog.
- `TaskMasterController` exposes `GET /products/categories`; MCP
  `get_categories` reads the same cache service.
- `ApplyForTaskMaster` and `NewTaskMaster` accept free-text category tags.
- `ApplicationController` copies submitted categories into the application and
  later into an approved profile. Catalog validation must cover both steps.
- `page-header` contains a hard-coded service list that can drift from the catalog.

## Phase 1: Admin-owned catalog

Tasks are ordered by dependency. Each checkbox is an implementation work item.

- [x] **1. Finalize the catalog contract.** Confirm collection/field naming,
  canonical ID format, duplicate handling, editable metadata, and response/error
  shapes. Preserve the existing category-ID arrays and exact-category search.
  Record the phase boundary and revised rollout order in the source spec.

- [x] **2. Add category persistence and shared domain logic.** Create the
  product-service model, repository, and service. Centralize catalog reads,
  normalization, duplicate checks, and validation of selected IDs. A category
  must exist independently of whether any TaskMaster currently uses it.

- [x] **3. Prepare a clean-reset catalog bootstrap.** Because current product
  data is disposable, reset only the `products` logical database instead of
  converting legacy category strings. Maintain an explicit, version-controlled
  canonical catalog, seed it before mock TaskMasters, and validate every mock
  category reference. Refuse startup when legacy profiles/applications exist
  without a catalog. Document the destructive reset scope and ensure the reset
  recreates the products database user without affecting other service data.

- [x] **4. Add admin category management APIs.** Support listing, creating, and
  editing category metadata through product-service. Keep IDs immutable and
  use explicit DTOs so clients cannot set audit fields. Enforce `ROLE_ADMIN`
  server-side using trusted JWT authorities, not a username or UI-only guard.
  Return clear validation, conflict, unauthenticated, and forbidden responses.
  Keep deletion, merging, and retirement out of this phase.

- [x] **5. Switch category discovery to the catalog.** Make
  `GET /products/categories` and MCP `get_categories` return canonical IDs from
  the same catalog, preserving their current string-array response shapes.
  Add a separate metadata response for selection/admin UIs needing display names
  and descriptions. Invalidate category caches after successful catalog writes;
  ensure old profile-derived Redis entries cannot survive the cutover. A newly
  created category must appear even when it has zero providers.

- [ ] **6. Enforce selection-only writes on the backend.** Validate nonempty
  category selections against the catalog during application submission, direct
  TaskMaster creation, and application acceptance. Revalidate pending
  applications before publishing their profiles. Normalize/deduplicate IDs under
  the agreed contract and reject unknown values with actionable errors. Audit
  other profile writers/imports so they cannot bypass the rule. Neither applicant
  nor admin profile creation may implicitly create catalog entries.

- [ ] **7. Add the admin category management screen.** Provide category list,
  create, and metadata-edit views with loading, empty, duplicate, and failure
  states. Add an admin navigation entry and connect frontend API methods through
  the existing `/products` BFF routing. Reuse authenticated bearer-token helpers;
  backend authorization remains authoritative.

- [ ] **8. Replace free-text category inputs.** Use a shared catalog-backed
  multi-select in both `ApplyForTaskMaster` and `NewTaskMaster`. Display readable
  names but submit canonical IDs. Handle loading failures and an empty catalog
  explicitly; prevent submission without a valid selection and never fall back
  to free text. No "Category not listed" submission path exists until Phase 2.

- [ ] **9. Align category consumers.** Use catalog metadata where category
  labels are shown in application review and profile flows. Replace the
  hard-coded header service list with catalog-backed entries without expanding
  unrelated navigation behavior. Preserve existing ID-based search URLs,
  request parameters, and stored references.

- [ ] **10. Add focused regression coverage.** Cover catalog persistence,
  normalized ID collisions, admin-only writes, unknown/empty selections,
  acceptance-time validation, reset/bootstrap repeatability, and cache refresh
  after changes.
  Add React coverage for catalog selection and admin management. Preserve MCP
  response compatibility and single-category search behavior. Add an E2E
  scenario: admin creates a category, applicant selects it, admin approves the
  application, and the resulting profile retains the canonical ID. Include API
  attempts to submit arbitrary categories without using the UI.

- [ ] **11. Roll out safely and document operations.** Approve and load the
  initial catalog, reset the disposable products database, then enable backend
  enforcement and selection-only forms in a coordinated release. Do not retain
  legacy product data under this rollout. Update seed data/setup documentation
  and explain how administrators create categories. Clear/version the category
  cache during deployment.

### Phase 1 completion criteria

Admins can create and maintain the database-backed category list. Applicants
and admin-created profiles select only catalog entries, with the same rules
enforced by the backend. Existing profiles and pending applications have an
explicit reset outcome. REST, MCP category discovery, and forms share one source
of truth. AI selection and search behavior remain unchanged.

## Phase 2: Missing-category requests (deferred)

- [ ] Add `Category not listed` with proposed name, service description, and
  optional example terms, linked to the application.
- [ ] Add proposal states `PENDING`, `APPROVED_NEW_CATEGORY`,
  `MAPPED_TO_EXISTING`, and `REJECTED`, with reviewer, timestamp, resolved
  category ID, and applicant-visible reason.
- [ ] Let admins create a canonical category or map the request to an existing
  category; support rejection with a reason. Alias approval requires the later
  taxonomy metadata support and must not silently create categories.
- [ ] Prevent publication/search visibility until all required category
  decisions are resolved. Store only resolved canonical IDs in profiles.
- [ ] Add applicant status/notifications, authorization and audit coverage,
  retry/concurrent-review protection, and end-to-end proposal-resolution tests.

## Phase 3: Search and AI evolution (deferred)

- [ ] Revisit whether to implement the interim ordered one-to-three-category
  search contract or proceed directly to the product-owned resolver.
- [ ] If implementing interim search, add canonical-ID validation, OR matching,
  deterministic ranking, deduplication, compatible tool arguments, diagnostics,
  and ordered cache keys before changing AI instructions.
- [ ] Add admin-owned aliases, object/skill terms, related categories, and
  taxonomy versioning for deterministic resolution.
- [ ] Implement `serviceQuery` resolution, unresolved-category errors, ranking,
  and cache behavior; then update AI instructions and remove prompt-specific
  object-to-category workarounds.

These later phases must not be bundled into the Phase 1 catalog refactor.
