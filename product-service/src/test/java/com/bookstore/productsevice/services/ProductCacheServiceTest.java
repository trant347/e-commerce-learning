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
}
