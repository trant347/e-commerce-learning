package com.bookstore.productsevice.services;

import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.fasterxml.jackson.databind.ObjectMapper;
import io.micrometer.core.instrument.simple.SimpleMeterRegistry;
import org.junit.Before;
import org.junit.Test;
import org.springframework.data.domain.Page;
import org.springframework.data.domain.PageImpl;
import org.springframework.data.domain.PageRequest;
import org.springframework.data.redis.core.Cursor;
import org.springframework.data.redis.core.RedisTemplate;
import org.springframework.data.redis.core.ScanOptions;
import org.springframework.data.redis.core.ValueOperations;

import java.util.*;
import java.util.stream.Collectors;
import java.util.stream.IntStream;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.*;
import static org.mockito.Mockito.*;

/**
 * Unit tests for ProductCacheService fragment caching logic.
 * Redis and MongoDB are fully mocked — no containers needed.
 */
public class ProductCacheServiceTest {

    private RedisTemplate<String, Object> redisTemplate;
    private ValueOperations<String, Object> valueOps;
    private TaskMasterRepository repository;
    private ProductCacheService cacheService;

    private TaskMaster sampleTm1;
    private TaskMaster sampleTm2;

    @Before
    @SuppressWarnings("unchecked")
    public void setUp() {
        redisTemplate = mock(RedisTemplate.class);
        valueOps = mock(ValueOperations.class);
        repository = mock(TaskMasterRepository.class);
        when(redisTemplate.opsForValue()).thenReturn(valueOps);

        cacheService = new ProductCacheService(redisTemplate, repository, new ObjectMapper(), new SimpleMeterRegistry());

        sampleTm1 = new TaskMaster()
                .setId("tm-1").setName("Alice").setLocation("New York")
                .setRating(4.5).setHourlyRateUsd(50.0)
                .setJobCategories(new String[]{"Plumbing"});

        sampleTm2 = new TaskMaster()
                .setId("tm-2").setName("Bob").setLocation("Chicago")
                .setRating(3.8).setHourlyRateUsd(35.0)
                .setJobCategories(new String[]{"Electrical"});
    }

    // ── getItemById ─────────────────────────────────────────────────────

    @Test
    public void getItemById_cacheHit_returnsFromRedis() {
        when(valueOps.get("products:item:tm-1")).thenReturn(sampleTm1);

        TaskMaster result = cacheService.getItemById("tm-1");

        assertThat(result).isNotNull();
        assertThat(result.getName()).isEqualTo("Alice");
        verify(repository, never()).findById(anyString());
    }

    @Test
    public void getItemById_cacheMiss_queriesDbAndCaches() {
        when(valueOps.get("products:item:tm-1")).thenReturn(null);
        when(repository.findById("tm-1")).thenReturn(Optional.of(sampleTm1));

        TaskMaster result = cacheService.getItemById("tm-1");

        assertThat(result).isNotNull();
        assertThat(result.getName()).isEqualTo("Alice");
        verify(repository).findById("tm-1");
        verify(valueOps).set(eq("products:item:tm-1"), eq(sampleTm1), anyLong(), any());
    }

    @Test
    public void getItemById_cacheMiss_notFoundInDb_returnsNull() {
        when(valueOps.get("products:item:tm-999")).thenReturn(null);
        when(repository.findById("tm-999")).thenReturn(Optional.empty());

        TaskMaster result = cacheService.getItemById("tm-999");

        assertThat(result).isNull();
    }

    @Test
    public void getItemById_redisThrows_fallsBackToDb() {
        when(valueOps.get("products:item:tm-1")).thenThrow(new RuntimeException("Redis down"));
        when(repository.findById("tm-1")).thenReturn(Optional.of(sampleTm1));

        TaskMaster result = cacheService.getItemById("tm-1");

        assertThat(result).isNotNull();
        assertThat(result.getName()).isEqualTo("Alice");
    }

    // ── getPage (fragment pattern) ──────────────────────────────────────

    @Test
    public void getPage_fullCacheHit_noDbCall() {
        // List cache returns IDs
        List<String> ids = List.of("tm-1", "tm-2");
        when(valueOps.get("products:list:page:0:limit:20")).thenReturn(ids);

        // MGET returns both items
        List<Object> items = List.of(sampleTm1, sampleTm2);
        when(valueOps.multiGet(List.of("products:item:tm-1", "products:item:tm-2")))
                .thenReturn(items);

        List<TaskMaster> result = cacheService.getPage(0, 20);

        assertThat(result).hasSize(2);
        assertThat(result.get(0).getName()).isEqualTo("Alice");
        assertThat(result.get(1).getName()).isEqualTo("Bob");
        verify(repository, never()).findAll(any(PageRequest.class));
        verify(repository, never()).findAllById(anyList());
    }

    @Test
    public void getPage_listCacheMiss_queriesDbAndCachesBoth() {
        // List cache miss
        when(valueOps.get("products:list:page:0:limit:20")).thenReturn(null);

        // DB returns items
        Page<TaskMaster> page = new PageImpl<>(List.of(sampleTm1, sampleTm2));
        when(repository.findAll(PageRequest.of(0, 20))).thenReturn(page);

        List<TaskMaster> result = cacheService.getPage(0, 20);

        assertThat(result).hasSize(2);
        // Verify list cache was stored
        verify(valueOps).set(eq("products:list:page:0:limit:20"), eq(List.of("tm-1", "tm-2")), anyLong(), any());
        // Verify individual items were cached
        verify(valueOps).set(eq("products:item:tm-1"), eq(sampleTm1), anyLong(), any());
        verify(valueOps).set(eq("products:item:tm-2"), eq(sampleTm2), anyLong(), any());
    }

    @Test
    public void getPage_partialMget_backfillsMissingFromDb() {
        // List cache returns IDs
        List<String> ids = List.of("tm-1", "tm-2");
        when(valueOps.get("products:list:page:0:limit:20")).thenReturn(ids);

        // MGET: tm-1 is cached, tm-2 is null (cache miss)
        List<Object> mgetResult = new ArrayList<>();
        mgetResult.add(sampleTm1);
        mgetResult.add(null);
        when(valueOps.multiGet(List.of("products:item:tm-1", "products:item:tm-2")))
                .thenReturn(mgetResult);

        // Backfill from DB
        when(repository.findAllById(List.of("tm-2"))).thenReturn(List.of(sampleTm2));

        List<TaskMaster> result = cacheService.getPage(0, 20);

        assertThat(result).hasSize(2);
        assertThat(result.get(0).getName()).isEqualTo("Alice");
        assertThat(result.get(1).getName()).isEqualTo("Bob");
        // Verify only the missing item was backfilled into cache
        verify(valueOps).set(eq("products:item:tm-2"), eq(sampleTm2), anyLong(), any());
    }

    @Test
    public void getPage_mgetThrows_fallsBackToDb() {
        List<String> ids = List.of("tm-1", "tm-2");
        when(valueOps.get("products:list:page:0:limit:20")).thenReturn(ids);
        when(valueOps.multiGet(anyList())).thenThrow(new RuntimeException("Redis down"));
        when(repository.findAllById(ids)).thenReturn(List.of(sampleTm1, sampleTm2));

        List<TaskMaster> result = cacheService.getPage(0, 20);

        assertThat(result).hasSize(2);
    }

    // ── Filter caches ───────────────────────────────────────────────────

    @Test
    public void getByCategory_cacheMiss_queriesDbAndCaches() {
        when(valueOps.get("products:filter:category:Plumbing")).thenReturn(null);
        when(repository.findAllByJobCategoriesContaining("Plumbing")).thenReturn(List.of(sampleTm1));

        List<TaskMaster> result = cacheService.getByCategory("Plumbing");

        assertThat(result).hasSize(1);
        assertThat(result.get(0).getName()).isEqualTo("Alice");
        verify(valueOps).set(eq("products:filter:category:Plumbing"), eq(List.of("tm-1")), anyLong(), any());
    }

    @Test
    public void getByLocation_cacheMiss_queriesDbAndCaches() {
        when(valueOps.get("products:filter:location:Chicago")).thenReturn(null);
        when(repository.findAllByLocation("Chicago")).thenReturn(List.of(sampleTm2));

        List<TaskMaster> result = cacheService.getByLocation("Chicago");

        assertThat(result).hasSize(1);
        assertThat(result.get(0).getName()).isEqualTo("Bob");
    }

    @Test
    public void getByName_emptyResult_cachesEmptyList() {
        when(valueOps.get("products:filter:name:Unknown")).thenReturn(null);
        when(repository.findAllByName("Unknown")).thenReturn(Collections.emptyList());

        List<TaskMaster> result = cacheService.getByName("Unknown");

        assertThat(result).isEmpty();
        verify(valueOps).set(eq("products:filter:name:Unknown"), eq(Collections.emptyList()), anyLong(), any());
    }

    // ── Categories cache ────────────────────────────────────────────────

    @Test
    public void getCategories_cacheHit_returnsFromRedis() {
        when(valueOps.get("products:categories")).thenReturn(List.of("Electrical", "Plumbing"));

        List<String> result = cacheService.getCategories();

        assertThat(result).containsExactly("Electrical", "Plumbing");
        verify(repository, never()).findAll();
    }

    @Test
    public void getCategories_cacheMiss_computesFromDbAndCaches() {
        when(valueOps.get("products:categories")).thenReturn(null);
        when(repository.findAll()).thenReturn(List.of(sampleTm1, sampleTm2));

        List<String> result = cacheService.getCategories();

        assertThat(result).containsExactly("Electrical", "Plumbing");
        verify(valueOps).set(eq("products:categories"), anyList(), anyLong(), any());
    }

    // ── Invalidation ────────────────────────────────────────────────────

    @SuppressWarnings("unchecked")
    @Test
    public void evictOnCreate_deletesListAndFilterKeysAndCategories() {
        Cursor<String> listCursor = mock(Cursor.class);
        Cursor<String> filterCursor = mock(Cursor.class);
        when(listCursor.hasNext()).thenReturn(true, false);
        when(listCursor.next()).thenReturn("products:list:page:0:limit:20");
        when(filterCursor.hasNext()).thenReturn(true, false);
        when(filterCursor.next()).thenReturn("products:filter:category:Plumbing");

        // Return different cursors for the two SCAN calls (list first, filter second)
        when(redisTemplate.scan(any(ScanOptions.class)))
                .thenReturn(listCursor, filterCursor);

        cacheService.evictOnCreate();

        verify(redisTemplate).delete(Set.of("products:list:page:0:limit:20"));
        verify(redisTemplate).delete(Set.of("products:filter:category:Plumbing"));
        verify(redisTemplate).delete("products:categories");
    }

    @SuppressWarnings("unchecked")
    @Test
    public void evictOnEdit_deletesItemAndFilterKeysAndCategories() {
        Cursor<String> filterCursor = mock(Cursor.class);
        when(filterCursor.hasNext()).thenReturn(false);
        when(redisTemplate.scan(any(ScanOptions.class))).thenReturn(filterCursor);

        cacheService.evictOnEdit("tm-1");

        verify(redisTemplate).delete("products:item:tm-1");
        verify(redisTemplate).delete("products:categories");
    }

    @SuppressWarnings("unchecked")
    @Test
    public void evictOnDelete_deletesItemAndAllListsAndFiltersAndCategories() {
        Cursor<String> cursor = mock(Cursor.class);
        when(cursor.hasNext()).thenReturn(false);
        when(redisTemplate.scan(any(ScanOptions.class))).thenReturn(cursor);

        cacheService.evictOnDelete("tm-1");

        verify(redisTemplate).delete("products:item:tm-1");
        verify(redisTemplate).delete("products:categories");
    }

    // ── searchWithFilters (used by MCP search_task_masters) ─────────────

    @Test
    public void searchWithFilters_cacheMiss_passesLimitToRepository() {
        String key = "products:filter:search:Plumbing:null:null:25.0:null:limit:10";
        when(valueOps.get(key)).thenReturn(null);
        when(repository.searchWithFilters("Plumbing", null, null, 25.0, null, 10))
                .thenReturn(List.of(sampleTm1));

        List<TaskMaster> result = cacheService.searchWithFilters(
                "Plumbing", null, null, 25.0, null, 10);

        assertThat(result).hasSize(1);
        verify(repository).searchWithFilters("Plumbing", null, null, 25.0, null, 10);
        verify(valueOps).set(eq(key), eq(List.of("tm-1")), anyLong(), any());
    }

    @Test
    public void searchWithFilters_cacheHit_skipsDb() {
        String key = "products:filter:search:Plumbing:New York:null:null:null:limit:10";
        when(valueOps.get(key)).thenReturn(List.of("tm-1"));
        when(valueOps.multiGet(List.of("products:item:tm-1")))
                .thenReturn(List.of(sampleTm1));

        List<TaskMaster> result = cacheService.searchWithFilters(
                "Plumbing", "New York", null, null, null, 10);

        assertThat(result).hasSize(1);
        assertThat(result.get(0).getName()).isEqualTo("Alice");
        verify(repository, never()).searchWithFilters(anyString(), anyString(), any(), any(), any(), anyInt());
    }

    @Test
    public void searchWithFilters_differentLimits_useDifferentCacheKeys() {
        String key5  = "products:filter:search:Plumbing:null:null:null:null:limit:5";
        String key10 = "products:filter:search:Plumbing:null:null:null:null:limit:10";

        when(valueOps.get(key5)).thenReturn(null);
        when(valueOps.get(key10)).thenReturn(null);
        when(repository.searchWithFilters("Plumbing", null, null, null, null, 5))
                .thenReturn(List.of(sampleTm1));
        when(repository.searchWithFilters("Plumbing", null, null, null, null, 10))
                .thenReturn(List.of(sampleTm1, sampleTm2));

        List<TaskMaster> r5 = cacheService.searchWithFilters("Plumbing", null, null, null, null, 5);
        List<TaskMaster> r10 = cacheService.searchWithFilters("Plumbing", null, null, null, null, 10);

        assertThat(r5).hasSize(1);
        assertThat(r10).hasSize(2);
        verify(valueOps).set(eq(key5), anyList(), anyLong(), any());
        verify(valueOps).set(eq(key10), anyList(), anyLong(), any());
    }

    // ── Upper-cap tests: ensure limit is honored end-to-end ─────────────

    @Test
    public void searchWithFilters_repositoryReturnsAtMostLimit_resultsAreCapped() {
        int limit = 10;
        // Repository (Mongo) is responsible for applying the limit; verify the service
        // surfaces exactly what it returns and never exceeds the cap.
        List<TaskMaster> capped = generateTaskMasters(limit);

        String key = "products:filter:search:Plumbing:null:null:null:null:limit:" + limit;
        when(valueOps.get(key)).thenReturn(null);
        when(repository.searchWithFilters("Plumbing", null, null, null, null, limit))
                .thenReturn(capped);

        List<TaskMaster> result = cacheService.searchWithFilters(
                "Plumbing", null, null, null, null, limit);

        assertThat(result).hasSize(limit);
        verify(repository).searchWithFilters("Plumbing", null, null, null, null, limit);
        // The cached id list should also contain at most `limit` ids
        List<String> expectedIds = capped.stream().map(TaskMaster::getId).collect(Collectors.toList());
        verify(valueOps).set(eq(key), eq(expectedIds), anyLong(), any());
    }

    @Test
    public void getByCategoryLimited_passesLimitAsPageable() {
        int limit = 10;
        when(valueOps.get("products:filter:category:Plumbing:limit:" + limit)).thenReturn(null);
        when(repository.findAllByJobCategoriesContaining(eq("Plumbing"), eq(PageRequest.of(0, limit))))
                .thenReturn(generateTaskMasters(limit));

        List<TaskMaster> result = cacheService.getByCategoryLimited("Plumbing", limit);

        assertThat(result).hasSize(limit);
        verify(repository).findAllByJobCategoriesContaining("Plumbing", PageRequest.of(0, limit));
        // Unlimited overload must not be invoked when the limited variant is requested
        verify(repository, never()).findAllByJobCategoriesContaining("Plumbing");
    }

    @Test
    public void getByLocationLimited_passesLimitAsPageable() {
        int limit = 10;
        when(valueOps.get("products:filter:location:Chicago:limit:" + limit)).thenReturn(null);
        when(repository.findAllByLocation(eq("Chicago"), eq(PageRequest.of(0, limit))))
                .thenReturn(generateTaskMasters(limit));

        List<TaskMaster> result = cacheService.getByLocationLimited("Chicago", limit);

        assertThat(result).hasSize(limit);
        verify(repository).findAllByLocation("Chicago", PageRequest.of(0, limit));
        verify(repository, never()).findAllByLocation("Chicago");
    }

    @Test
    public void getByRateRangeLimited_passesLimitAsPageable() {
        int limit = 10;
        when(valueOps.get("products:filter:rate:0.0:25.0:limit:" + limit)).thenReturn(null);
        when(repository.findTaskMasterByHourlyRateUsdBetween(eq(0.0), eq(25.0), eq(PageRequest.of(0, limit))))
                .thenReturn(generateTaskMasters(limit));

        List<TaskMaster> result = cacheService.getByRateRangeLimited(0.0, 25.0, limit);

        assertThat(result).hasSize(limit);
        verify(repository).findTaskMasterByHourlyRateUsdBetween(0.0, 25.0, PageRequest.of(0, limit));
    }

    @Test
    public void getByMinRatingLimited_passesLimitAsPageable() {
        int limit = 10;
        when(valueOps.get("products:filter:rating:4.0:limit:" + limit)).thenReturn(null);
        when(repository.findTaskMasterByRatingGreaterThanEqual(eq(4.0), eq(PageRequest.of(0, limit))))
                .thenReturn(generateTaskMasters(limit));

        List<TaskMaster> result = cacheService.getByMinRatingLimited(4.0, limit);

        assertThat(result).hasSize(limit);
        verify(repository).findTaskMasterByRatingGreaterThanEqual(4.0, PageRequest.of(0, limit));
    }

    @Test
    public void searchWithFilters_repositoryReturnsFewerThanLimit_returnsAll() {
        int limit = 10;
        List<TaskMaster> few = generateTaskMasters(3);

        String key = "products:filter:search:Plumbing:null:null:null:null:limit:" + limit;
        when(valueOps.get(key)).thenReturn(null);
        when(repository.searchWithFilters("Plumbing", null, null, null, null, limit))
                .thenReturn(few);

        List<TaskMaster> result = cacheService.searchWithFilters(
                "Plumbing", null, null, null, null, limit);

        assertThat(result).hasSize(3);
    }

    // ── Order preservation ──────────────────────────────────────────────

    @Test
    public void getPage_preservesIdOrder() {
        // IDs in specific order
        List<String> ids = List.of("tm-2", "tm-1");
        when(valueOps.get("products:list:page:0:limit:20")).thenReturn(ids);

        // MGET returns in same order as keys
        List<Object> items = List.of(sampleTm2, sampleTm1);
        when(valueOps.multiGet(List.of("products:item:tm-2", "products:item:tm-1")))
                .thenReturn(items);

        List<TaskMaster> result = cacheService.getPage(0, 20);

        assertThat(result).hasSize(2);
        assertThat(result.get(0).getName()).isEqualTo("Bob");
        assertThat(result.get(1).getName()).isEqualTo("Alice");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static List<TaskMaster> generateTaskMasters(int count) {
        return IntStream.range(0, count)
                .mapToObj(i -> new TaskMaster()
                        .setId("tm-cap-" + i)
                        .setName("Provider " + i)
                        .setLocation("New York")
                        .setRating(4.5)
                        .setHourlyRateUsd(20.0)
                        .setJobCategories(new String[]{"Plumbing"}))
                .collect(Collectors.toList());
    }
}
