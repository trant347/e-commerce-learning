# Redis Caching Spec for GET Products Requests

## 1. Problem Analysis

### 1.1 Current Request Flow

Every GET product request follows this path today:

```
Frontend / AI Assistant  →  HTTP  →  product-service  →  MongoDB query  →  response
```

The **ai-assistant-service** adds an extra hop — it calls product-service over HTTP, which then queries MongoDB. So a single AI chat interaction that needs product data results in:

```
User → ai-assistant-service → HTTP round-trip → product-service → MongoDB query → response back up the chain
```

### 1.2 Why This Is a Problem

1. **Repeated identical queries** — Product data (TaskMaster profiles) changes infrequently (only on create), but every GET request hits MongoDB. The frontend product listing page, search, and category filters all trigger fresh database queries on every page load or interaction.

2. **AI assistant latency compounds** — The ai-assistant-service is already slow due to LLM inference (Ollama with 180s timeout). Adding an HTTP round-trip to product-service plus a MongoDB query on top of that makes the total response time worse. The product data fetched is often the same across chat sessions.

3. **MongoDB load scales linearly** — Without caching, every concurrent user browsing products or chatting with the AI assistant generates a separate MongoDB query. As user count grows, MongoDB becomes a bottleneck even though the data being served is identical.

4. **The `/products/categories` endpoint is especially wasteful** — It fetches *all* TaskMaster documents from MongoDB, then extracts and deduplicates categories in Java. This is an expensive full-collection scan that returns a small, rarely-changing list.

### 1.3 Why Redis

- **Already in the stack** — Redis (`redis:6.0.9`) is already defined in `docker-compose.yml` on port 6379 but currently unused by any service. No new infrastructure to provision.
- **Sub-millisecond reads** — Redis serves cached data in <1ms vs MongoDB's typical 5-50ms+ query time, plus it eliminates network hops between services.
- **TTL support** — Built-in key expiration means stale data is automatically cleaned up without manual intervention.
- **Shared across services** — Both product-service (Java) and ai-assistant-service (C#) can connect to the same Redis instance, each caching at their own layer.
- **MGET support** — Redis can fetch multiple keys in a single round-trip, enabling the fragment caching pattern without N+1 call overhead.

---

## 2. Caching Strategy: Fragment Caching (Amazon-Style)

### 2.1 Core Concept: Separate "What's in the List" from "What's in Each Item"

Instead of caching entire API responses as monolithic blobs, we split the cache into two types of entries:

- **Item cache** — Each individual TaskMaster is cached by its ID. Long TTL (30 min). Surgically invalidated when that specific item is edited.
- **List cache** — Paginated list queries cache only the **list of IDs** (not the full objects). Short TTL (5 min). Cheaply invalidated on create/delete since the list is just an array of strings.

This is the same pattern used by Amazon, where a "promotions page" is not cached as one giant blob — instead, the *membership* of the page (which product IDs) is cached separately from the *product data* for each ID.

### 2.2 How a Request Flows Through the Cache

```
GET /products?page=0&limit=20
```

**Step 1: Check list cache**
```
GET "products:list:page:0:limit:20"  →  ["id1", "id2", ..., "id20"]    (1 Redis call)
```
- Cache HIT → proceed to Step 2
- Cache MISS → query MongoDB for the page, extract the IDs, store them with 5-min TTL, proceed to Step 2

**Step 2: Multi-get item cache**
```
MGET "products:item:id1" "products:item:id2" ... "products:item:id20"   (1 Redis call)
```
- All HITs → return assembled response (total: 2 Redis calls, 0 MongoDB calls) ✅
- Partial HITs → query MongoDB only for the missing IDs, backfill them into Redis with 30-min TTL, return assembled response
- All MISSes → query MongoDB for all IDs, populate cache, return response

```
                        GET /products?page=0&limit=20
                                    │
                                    ▼
                    ┌───────────────────────────────┐
                    │  Step 1: List Cache Lookup     │
                    │  Key: "products:list:page:0"  │
                    └───────────┬───────────────────┘
                                │
                    ┌───── HIT ─┤─── MISS ─────┐
                    │           │               │
                    ▼           │               ▼
            [id1, id2, ...]    │     Query MongoDB for page
                    │           │     Store IDs → 5-min TTL
                    │           │               │
                    ◄───────────┘───────────────┘
                    │
                    ▼
                    ┌───────────────────────────────┐
                    │  Step 2: Item Multi-Get        │
                    │  MGET products:item:id1 ...    │
                    └───────────┬───────────────────┘
                                │
              ┌──── All HIT ────┼──── Partial/Miss ───┐
              │                 │                      │
              ▼                 │                      ▼
        Return response         │        Query MongoDB for missing IDs
        (0 DB calls)            │        Backfill cache → 30-min TTL
                                │                      │
                                ◄──────────────────────┘
                                │
                                ▼
                         Return assembled response
```

### 2.3 Why This Is Better Than Monolithic Caching

| Concern | Monolithic (cache full page JSON) | Fragment (cache IDs + items separately) |
|---|---|---|
| **TaskMaster edited** | Evict ALL page caches (don't know which pages are affected) → expensive rebuild | Evict only `products:item:{id}` → 1 key. List caches unaffected (same IDs, item re-fetched on next MGET) |
| **New TaskMaster created** | Evict ALL page caches → every page repopulated from MongoDB | Evict only list caches (just arrays of IDs, cheap). Existing item caches untouched |
| **TaskMaster deleted** | Evict ALL page caches | Evict the item key + list caches |
| **Cache memory** | Duplicate data — same TaskMaster appears in multiple page caches | Each TaskMaster stored once. List caches are tiny (just ID arrays) |
| **Cache hit rate** | A single mutation blows up everything | Most items survive mutations. Only the changed item or list structure is affected |

### 2.4 TTL Strategy

| Cache Type | TTL | Rationale |
|---|---|---|
| **Item cache** (`products:item:{id}`) | 30 minutes | Product data changes infrequently. Surgically invalidated on edit, so TTL is just a safety net |
| **List/page cache** (`products:list:*`) | 5 minutes | Short because list membership (which IDs on which page) can shift when products are added/removed. Cheap to rebuild (just a MongoDB query returning IDs) |
| **Filter cache** (`products:filter:*`) | 5 minutes | Same reasoning as list cache — filter results shift when products change |
| **Categories cache** (`products:categories`) | 30 minutes | Rarely changes, expensive to compute (full collection scan). Invalidated explicitly on create/edit |

---

## 3. Cache Key Design

### 3.1 product-service (Layer 1 — avoids MongoDB)

| Redis Key | Value | TTL | Source |
|---|---|---|---|
| `products:item:{id}` | Full TaskMaster JSON | 30 min | `GET /products/{id}` or backfilled from list queries |
| `products:list:page:{p}:limit:{l}` | JSON array of IDs: `["id1","id2",...]` | 5 min | `GET /products?page=0&limit=20` |
| `products:filter:name:{name}` | JSON array of IDs | 5 min | `GET /products?name=X` |
| `products:filter:location:{loc}` | JSON array of IDs | 5 min | `GET /products?location=X` |
| `products:filter:category:{cat}` | JSON array of IDs | 5 min | `GET /products?category=X` |
| `products:filter:rating:{min}` | JSON array of IDs | 5 min | `GET /products/by-rating?minRating=X` |
| `products:filter:rate:{min}:{max}` | JSON array of IDs | 5 min | `GET /products/by-rate-range` |
| `products:categories` | JSON array of strings | 30 min | `GET /products/categories` |

### 3.2 ai-assistant-service (Layer 2 — avoids HTTP round-trip)

| Redis Key | Value | TTL | Source |
|---|---|---|---|
| `ai:products:all` | Raw HTTP response body | 5 min | `GET /products` |
| `ai:products:category:{cat}` | Raw HTTP response body | 5 min | `GET /products?category=X` |
| `ai:products:location:{loc}` | Raw HTTP response body | 5 min | `GET /products?location=X` |
| `ai:products:categories` | Raw HTTP response body | 30 min | `GET /products/categories` |

> **Note:** The ai-assistant-service uses simpler monolithic caching (full response body) because it only needs the data as raw text for LLM context. Fragment caching adds no benefit here — the service doesn't need to surgically update individual items.

---

## 4. Cache Invalidation Rules

### 4.1 On TaskMaster Created (`POST /products`)

```
1. Evict all list/filter caches     →  DEL products:list:*  products:filter:*
2. Evict categories cache           →  DEL products:categories
3. Item caches left untouched       →  existing items are still valid
```

**Why:** A new product changes which IDs appear in list/filter results and may introduce new categories. Existing product data hasn't changed.

### 4.2 On TaskMaster Edited (future `PUT /products/{id}`)

```
1. Evict the specific item cache    →  DEL products:item:{id}
2. Evict categories cache           →  DEL products:categories  (category may have changed)
3. Evict filter caches              →  DEL products:filter:*    (location/rating may have changed)
4. List page caches left untouched  →  same IDs, MGET will re-fetch the edited item
```

**Why:** The item data changed, but the *membership* of paginated lists (which IDs are on which page) hasn't — the item just has different content now. Filter caches are evicted because the item may no longer match old filter criteria.

> **This is the key advantage of fragment caching** — editing a product evicts 1 item key instead of blowing up every page cache.

### 4.3 On TaskMaster Deleted (future `DELETE /products/{id}`)

```
1. Evict the specific item cache    →  DEL products:item:{id}
2. Evict all list/filter caches     →  DEL products:list:*  products:filter:*
3. Evict categories cache           →  DEL products:categories
```

**Why:** The item is gone, and list membership has shifted (page boundaries change).

---

## 5. Two-Layer Architecture

```
                                    ┌─────────────────────────┐
                                    │      Redis (shared)     │
                                    │                         │
                                    │  products:item:{id}     │  ← individual TaskMasters (30 min TTL)
                                    │  products:list:page:*   │  ← page ID lists (5 min TTL)
                                    │  products:filter:*      │  ← filter ID lists (5 min TTL)
                                    │  products:categories    │  ← category list (30 min TTL)
                                    │  ai:products:*          │  ← ai-assistant HTTP responses (5 min TTL)
                                    └─────┬───────────┬───────┘
                                          │           │
              ┌───────────────────────────┤           ├───────────────────────────┐
              │                           │           │                           │
   ┌──────────▼──────────┐     ┌──────────▼──────────┐               ┌───────────▼──────────┐
   │   product-service   │     │ ai-assistant-service │               │      Frontend        │
   │                     │     │                      │               │                      │
   │  Fragment caching:  │     │  Response caching:   │               │  Benefits via faster │
   │  - List of IDs      │     │  - Raw HTTP body     │               │  product-service     │
   │  - MGET items       │     │  - Skip HTTP call    │               │  responses           │
   │  - Surgical evict   │     │  - Fallback to HTTP  │               │                      │
   └─────────────────────┘     └──────────────────────┘               └───────────────────────┘
```

### 5.1 Why Two Layers

- **Layer 1 (product-service):** Fragment caching avoids MongoDB queries. Benefits all consumers.
- **Layer 2 (ai-assistant-service):** Simple response caching avoids the HTTP round-trip entirely. This is valuable because even a Redis-backed product-service response still has 5-20ms of HTTP overhead, and the AI assistant makes the same queries across many chat sessions.

### 5.2 Why Different Strategies Per Layer

The ai-assistant-service does **not** use fragment caching because:
- It consumes product data as raw text for LLM context — it doesn't need per-item granularity
- It only calls 4 endpoints — simple key space
- Surgical invalidation doesn't matter here — the 5-min TTL is short enough

---

## 6. What We Do NOT Cache

| Endpoint | Why Not |
|---|---|
| `GET /products/me/taskmaster` | User-specific — low cache hit rate, requires per-user keys |
| `GET /products/facet-search` | Complex aggregation with sort fields — high key cardinality, low hit rate. Revisit later if needed |
| `POST /products` | Mutation — triggers invalidation, not caching |
| `POST /products/tests` | Test endpoint |

---

## 7. Graceful Degradation

All cache operations must be wrapped in try-catch with fallback to the current behavior (direct MongoDB query or HTTP call). Redis is an optimization, not a dependency.

| Failure | Behavior |
|---|---|
| Redis connection refused | Fall through to MongoDB/HTTP as if cache doesn't exist. Log warning. |
| Redis timeout on GET | Treat as cache miss. Query MongoDB. |
| Redis timeout on SET | Skip caching. Return the response normally. |
| Partial MGET failure | Fetch missing items from MongoDB. Don't fail the whole request. |

---

## 8. Expected Impact

| Metric | Before | After (cache hit) |
|---|---|---|
| `GET /products?page=0&limit=20` | 10-50ms (MongoDB) | <1ms (2 Redis calls) |
| `GET /products/{id}` | 5-20ms (MongoDB) | <1ms (1 Redis call) |
| `GET /products/categories` | 50-200ms (full scan) | <1ms (1 Redis call) |
| AI assistant product fetch | 20-100ms (HTTP + MongoDB) | <1ms (1 Redis call) |
| Impact of single product edit | N/A | Evicts 1 item key (not all pages) |
| Impact of product creation | N/A | Evicts list keys only (cheap ID arrays, not full data) |
| MongoDB read load | Linear with users | ~1 query per unique page per 5 min + ~1 per unique item per 30 min |

---

## 9. Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Stale item data after edit | Surgical eviction on mutation + 30-min TTL safety net |
| Stale list after create/delete | List caches evicted on mutation + 5-min TTL safety net |
| Redis downtime | Graceful fallback to MongoDB/HTTP (Section 7) |
| Cache stampede on cold start | Low risk at current scale. Can add distributed locking (Redisson) later if needed |
| MGET returns partial nulls | Fetch missing items from MongoDB, backfill into cache |
| Two layers out of sync | Independent TTLs and key prefixes. ai-assistant layer is just an optimization over HTTP |
| Complexity vs. simple caching | This is a learning project — the goal is to practice the Amazon-style pattern |
