package com.bookstore.productsevice.services;

import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.data.domain.PageRequest;
import org.springframework.data.redis.core.Cursor;
import org.springframework.data.redis.core.RedisTemplate;
import org.springframework.data.redis.core.ScanOptions;
import org.springframework.stereotype.Service;

import java.util.*;
import java.util.concurrent.TimeUnit;
import java.util.function.Supplier;
import java.util.stream.Collectors;

/**
 * Amazon-style fragment caching for TaskMaster products.
 *
 * List/filter queries cache an array of IDs (short TTL).
 * Individual items are cached by ID (long TTL).
 * Responses are assembled via Redis MGET, with partial-miss backfill from MongoDB.
 */
@Service
public class ProductCacheService {

    private static final Logger log = LoggerFactory.getLogger(ProductCacheService.class);

    private static final long ITEM_TTL_MINUTES = 30;
    private static final long LIST_TTL_MINUTES = 5;

    static final String ITEM_PREFIX = "products:item:";
    static final String LIST_PREFIX = "products:list:";
    static final String FILTER_PREFIX = "products:filter:";
    static final String CATEGORIES_KEY = "products:categories";

    private final RedisTemplate<String, Object> redisTemplate;
    private final TaskMasterRepository repository;
    private final ObjectMapper objectMapper;

    public ProductCacheService(RedisTemplate<String, Object> redisTemplate,
                               TaskMasterRepository repository,
                               ObjectMapper objectMapper) {
        this.redisTemplate = redisTemplate;
        this.repository = repository;
        this.objectMapper = objectMapper;
    }

    // ── Item cache ──────────────────────────────────────────────────────

    public TaskMaster getItemById(String id) {
        String key = ITEM_PREFIX + id;
        TaskMaster cached = getItemFromCache(key);
        if (cached != null) {
            log.debug("[Cache] HIT item {}", id);
            return cached;
        }
        log.debug("[Cache] MISS item {}", id);
        Optional<TaskMaster> fromDb = repository.findById(id);
        fromDb.ifPresent(this::cacheItem);
        return fromDb.orElse(null);
    }

    // ── Paginated list cache (fragment pattern) ─────────────────────────

    public List<TaskMaster> getPage(int page, int limit) {
        String listKey = LIST_PREFIX + "page:" + page + ":limit:" + limit;
        return getViaFragmentCache(listKey, () -> {
            List<TaskMaster> items = repository.findAll(PageRequest.of(page, limit)).getContent();
            return new ArrayList<>(items);
        });
    }

    // ── Filter caches ───────────────────────────────────────────────────

    public List<TaskMaster> getByName(String name) {
        String listKey = FILTER_PREFIX + "name:" + name;
        return getViaFragmentCache(listKey, () -> repository.findAllByName(name));
    }

    public List<TaskMaster> getByLocation(String location) {
        String listKey = FILTER_PREFIX + "location:" + location;
        return getViaFragmentCache(listKey, () -> repository.findAllByLocation(location));
    }

    public List<TaskMaster> getByCategory(String category) {
        String listKey = FILTER_PREFIX + "category:" + category;
        return getViaFragmentCache(listKey, () -> repository.findAllByJobCategoriesContaining(category));
    }

    public List<TaskMaster> getByMinRating(double minRating) {
        String listKey = FILTER_PREFIX + "rating:" + minRating;
        return getViaFragmentCache(listKey, () -> repository.findTaskMasterByRatingGreaterThanEqual(minRating));
    }

    public List<TaskMaster> getByRateRange(double minRate, double maxRate) {
        String listKey = FILTER_PREFIX + "rate:" + minRate + ":" + maxRate;
        return getViaFragmentCache(listKey, () -> repository.findTaskMasterByHourlyRateUsdBetween(minRate, maxRate));
    }

    // ── Categories cache ────────────────────────────────────────────────

    @SuppressWarnings("unchecked")
    public List<String> getCategories() {
        try {
            Object cached = redisTemplate.opsForValue().get(CATEGORIES_KEY);
            if (cached instanceof List<?> list) {
                log.debug("[Cache] HIT categories");
                return list.stream().map(Object::toString).collect(Collectors.toList());
            }
        } catch (Exception e) {
            log.warn("[Cache] Redis read failed for categories, falling through to DB", e);
        }

        log.debug("[Cache] MISS categories");
        List<String> categories = repository.findAll().stream()
                .filter(tm -> tm.getJobCategories() != null)
                .flatMap(tm -> Arrays.stream(tm.getJobCategories()))
                .filter(c -> c != null && !c.isBlank())
                .distinct()
                .sorted()
                .collect(Collectors.toList());

        try {
            redisTemplate.opsForValue().set(CATEGORIES_KEY, categories, ITEM_TTL_MINUTES, TimeUnit.MINUTES);
        } catch (Exception e) {
            log.warn("[Cache] Redis write failed for categories", e);
        }
        return categories;
    }

    // ── Invalidation ────────────────────────────────────────────────────

    /** Called after a new product is created. */
    public void evictOnCreate() {
        log.info("[Cache] Evicting list/filter caches and categories after product creation");
        evictByPattern(LIST_PREFIX + "*");
        evictByPattern(FILTER_PREFIX + "*");
        deleteKey(CATEGORIES_KEY);
    }

    /** Called after a product is edited (future use). */
    public void evictOnEdit(String id) {
        log.info("[Cache] Evicting caches after product edit id={}", id);
        deleteKey(ITEM_PREFIX + id);
        evictByPattern(FILTER_PREFIX + "*");
        deleteKey(CATEGORIES_KEY);
    }

    /** Called after a product is deleted (future use). */
    public void evictOnDelete(String id) {
        log.info("[Cache] Evicting caches after product delete id={}", id);
        deleteKey(ITEM_PREFIX + id);
        evictByPattern(LIST_PREFIX + "*");
        evictByPattern(FILTER_PREFIX + "*");
        deleteKey(CATEGORIES_KEY);
    }

    // ── Core fragment cache logic ───────────────────────────────────────

    /**
     * Fragment cache pattern:
     * 1. Check Redis for a list of IDs under the given key
     * 2. On hit → MGET individual items, backfill misses from MongoDB
     * 3. On miss → query MongoDB, cache IDs (short TTL) + items (long TTL)
     */
    private List<TaskMaster> getViaFragmentCache(String listKey, Supplier<List<TaskMaster>> dbQuery) {
        // Step 1: Try to get ID list from cache
        List<String> cachedIds = getIdListFromCache(listKey);

        if (cachedIds != null) {
            log.debug("[Cache] HIT list key={}", listKey);
            return assembleFromIds(cachedIds);
        }

        // Cache miss — query MongoDB
        log.debug("[Cache] MISS list key={}", listKey);
        List<TaskMaster> items = dbQuery.get();

        // Cache the results
        List<String> ids = items.stream()
                .map(TaskMaster::getId)
                .collect(Collectors.toList());
        cacheIdList(listKey, ids);
        items.forEach(this::cacheItem);

        return items;
    }

    /**
     * Assemble full TaskMaster objects from a list of IDs using MGET.
     * Backfills any cache misses from MongoDB.
     */
    private List<TaskMaster> assembleFromIds(List<String> ids) {
        if (ids.isEmpty()) {
            return Collections.emptyList();
        }

        // Build keys for MGET
        List<String> keys = ids.stream()
                .map(id -> ITEM_PREFIX + id)
                .collect(Collectors.toList());

        // MGET all items
        List<Object> values;
        try {
            values = redisTemplate.opsForValue().multiGet(keys);
        } catch (Exception e) {
            log.warn("[Cache] MGET failed, falling back to DB", e);
            return new ArrayList<>(repository.findAllById(ids));
        }

        if (values == null) {
            return new ArrayList<>(repository.findAllById(ids));
        }

        // Identify hits and misses, preserving order
        Map<String, TaskMaster> resultMap = new LinkedHashMap<>();
        List<String> missingIds = new ArrayList<>();

        for (int i = 0; i < ids.size(); i++) {
            String id = ids.get(i);
            Object value = values.get(i);
            TaskMaster item = convertToTaskMaster(value);
            if (item != null) {
                resultMap.put(id, item);
            } else {
                missingIds.add(id);
            }
        }

        // Backfill misses from MongoDB
        if (!missingIds.isEmpty()) {
            log.debug("[Cache] Backfilling {} missing items from MongoDB", missingIds.size());
            List<TaskMaster> fromDb = repository.findAllById(missingIds);
            for (TaskMaster tm : fromDb) {
                resultMap.put(tm.getId(), tm);
                cacheItem(tm);
            }
        }

        // Assemble in original ID order
        List<TaskMaster> result = new ArrayList<>(ids.size());
        for (String id : ids) {
            TaskMaster tm = resultMap.get(id);
            if (tm != null) {
                result.add(tm);
            }
        }
        return result;
    }

    // ── Redis helpers ───────────────────────────────────────────────────

    private void cacheItem(TaskMaster item) {
        try {
            redisTemplate.opsForValue().set(
                    ITEM_PREFIX + item.getId(), item, ITEM_TTL_MINUTES, TimeUnit.MINUTES);
        } catch (Exception e) {
            log.warn("[Cache] Failed to cache item id={}", item.getId(), e);
        }
    }

    @SuppressWarnings("unchecked")
    private List<String> getIdListFromCache(String key) {
        try {
            Object cached = redisTemplate.opsForValue().get(key);
            if (cached instanceof List<?> list) {
                return list.stream().map(Object::toString).collect(Collectors.toList());
            }
        } catch (Exception e) {
            log.warn("[Cache] Redis read failed for key={}", key, e);
        }
        return null;
    }

    private void cacheIdList(String key, List<String> ids) {
        try {
            redisTemplate.opsForValue().set(key, ids, LIST_TTL_MINUTES, TimeUnit.MINUTES);
        } catch (Exception e) {
            log.warn("[Cache] Failed to cache ID list key={}", key, e);
        }
    }

    private TaskMaster getItemFromCache(String key) {
        try {
            Object value = redisTemplate.opsForValue().get(key);
            return convertToTaskMaster(value);
        } catch (Exception e) {
            log.warn("[Cache] Redis read failed for key={}", key, e);
            return null;
        }
    }

    private TaskMaster convertToTaskMaster(Object value) {
        if (value == null) return null;
        if (value instanceof TaskMaster tm) return tm;
        try {
            return objectMapper.convertValue(value, TaskMaster.class);
        } catch (Exception e) {
            log.warn("[Cache] Failed to convert cached value to TaskMaster", e);
            return null;
        }
    }

    private void deleteKey(String key) {
        try {
            redisTemplate.delete(key);
        } catch (Exception e) {
            log.warn("[Cache] Failed to delete key={}", key, e);
        }
    }

    /** Evict all keys matching a pattern using SCAN (non-blocking). */
    private void evictByPattern(String pattern) {
        try {
            Set<String> keysToDelete = new HashSet<>();
            ScanOptions options = ScanOptions.scanOptions().match(pattern).count(100).build();
            try (Cursor<String> cursor = redisTemplate.scan(options)) {
                while (cursor.hasNext()) {
                    keysToDelete.add(cursor.next());
                }
            }
            if (!keysToDelete.isEmpty()) {
                redisTemplate.delete(keysToDelete);
                log.debug("[Cache] Evicted {} keys matching pattern={}", keysToDelete.size(), pattern);
            }
        } catch (Exception e) {
            log.warn("[Cache] Failed to evict keys matching pattern={}", pattern, e);
        }
    }
}
