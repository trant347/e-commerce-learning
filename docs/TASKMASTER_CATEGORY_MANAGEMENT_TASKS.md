# TaskMaster Category Management Completion Record

Status: **Historical record. Original catalog phase (tasks 1-11) complete.**

Source: [TaskMaster Category Resolution Specification](TASKMASTER_CATEGORY_RESOLUTION_SPEC.md).

The source specification is authoritative for current contracts and the new
hybrid AI rollout. The phase numbers below refer to the original catalog
project, not the new hybrid implementation phases. The historical requirement
to implement missing-category proposals before AI improvements is withdrawn.
No database reset is required for the hybrid rollout.

## Completed scope

The catalog phase introduced the admin-managed, database-backed catalog and
selection-only forms. It did not change AI prompts, search-tool arguments,
ranking, category limits, or natural-language resolution. Single-category
search remains the contract; the previous proposed expansion to up to three
categories has been superseded.

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

Aliases were excluded from this completed phase and are now proposed in the
hybrid specification. Object terms, related-category scoring, merging,
retirement, and required profile attributes remain outside the current rollout.
Removing referenced categories requires an explicit reassignment policy.

## Implementation before original Phase 1

- `ProductCacheService.getCategories()` derives categories from existing
  TaskMaster profiles rather than an independent catalog.
- `TaskMasterController` exposes `GET /products/categories`; MCP
  `get_categories` reads the same cache service.
- `ApplyForTaskMaster` and `NewTaskMaster` accept free-text category tags.
- `ApplicationController` copies submitted categories into the application and
  later into an approved profile. Catalog validation must cover both steps.
- `page-header` contains a hard-coded service list that can drift from the catalog.

## Original Phase 1: Admin-owned catalog

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

- [x] **6. Enforce selection-only writes on the backend.** Validate nonempty
  category selections against the catalog during application submission, direct
  TaskMaster creation, and application acceptance. Revalidate pending
  applications before publishing their profiles. Normalize/deduplicate IDs under
  the agreed contract and reject unknown values with actionable errors. Audit
  other profile writers/imports so they cannot bypass the rule. Neither applicant
  nor admin profile creation may implicitly create catalog entries.

- [x] **7. Add the admin category management screen.** Provide category list,
  create, and metadata-edit views with loading, empty, duplicate, and failure
  states. Add an admin navigation entry and connect frontend API methods through
  the existing `/products` BFF routing. Reuse authenticated bearer-token helpers;
  backend authorization remains authoritative.

- [x] **8. Replace free-text category inputs.** Use a shared catalog-backed
  multi-select in both `ApplyForTaskMaster` and `NewTaskMaster`. Display readable
  names but submit canonical IDs. Handle loading failures and an empty catalog
  explicitly; prevent submission without a valid selection and never fall back
  to free text. No "Category not listed" submission path exists until the
  deferred missing-category proposal feature is implemented.

- [x] **9. Align category consumers.** Use catalog metadata where category
  labels are shown in application review and profile flows. Replace the
  hard-coded header service list with catalog-backed entries without expanding
  unrelated navigation behavior. Preserve existing ID-based search URLs,
  request parameters, and stored references.

- [x] **10. Add focused regression coverage.** Cover catalog persistence,
  normalized ID collisions, admin-only writes, unknown/empty selections,
  acceptance-time validation, reset/bootstrap repeatability, and cache refresh
  after changes.
  Add React coverage for catalog selection and admin management. Preserve MCP
  response compatibility and single-category search behavior. Add an E2E
  scenario: admin creates a category, applicant selects it, admin approves the
  application, and the resulting profile retains the canonical ID. Include API
  attempts to submit arbitrary categories without using the UI.

- [x] **11. Roll out safely and document operations.** Approve and load the
  initial catalog, reset the disposable products database, then enable backend
  enforcement and selection-only forms in a coordinated release. Do not retain
  legacy product data under this rollout. Update seed data/setup documentation
  and explain how administrators create categories. Clear/version the category
  cache during deployment.

### Original Phase 1 completion criteria

Admins can create and maintain the database-backed category list. Applicants
and admin-created profiles select only catalog entries, with the same rules
enforced by the backend. Existing profiles and pending applications have an
explicit reset outcome. REST, MCP category discovery, and forms share one source
of truth. AI selection and search behavior remain unchanged.

## Future work: consolidated in the source specification

The original Phase 2/3 checklists are superseded, not completed. See the source
specification for the proposed hybrid phases, acceptance criteria, and the
separate deferred missing-category proposal requirements.

The current direction retains single-category search, adds rich catalog
metadata and clarification first, then introduces conservative exact matching
with LLM interpretation for other requests. There is no requirement to build
multi-category search, object/action scoring, or a broad rules engine.
