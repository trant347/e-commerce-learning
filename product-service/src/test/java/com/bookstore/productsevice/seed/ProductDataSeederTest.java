package com.bookstore.productsevice.seed;

import com.bookstore.productsevice.category.CategoryService;
import com.bookstore.productsevice.location.LocationNormalizer;
import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.ApplicationRepository;
import com.bookstore.productsevice.repository.CategoryRepository;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.bookstore.productsevice.services.ProductCacheService;
import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.Before;
import org.junit.Test;
import org.mockito.ArgumentCaptor;
import org.mockito.InOrder;
import org.springframework.core.io.ClassPathResource;

import java.io.InputStream;
import java.util.ArrayList;
import java.util.Collection;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.Mockito.inOrder;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.times;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

public class ProductDataSeederTest {

    private CategoryRepository categoryRepository;
    private TaskMasterRepository taskMasterRepository;
    private ApplicationRepository applicationRepository;
    private CategoryService categoryService;
    private ProductCacheService productCacheService;
    private ProductDataSeeder seeder;

    @Before
    @SuppressWarnings("unchecked")
    public void setUp() {
        categoryRepository = mock(CategoryRepository.class);
        taskMasterRepository = mock(TaskMasterRepository.class);
        applicationRepository = mock(ApplicationRepository.class);
        categoryService = mock(CategoryService.class);
        productCacheService = mock(ProductCacheService.class);

        ObjectMapper objectMapper = new ObjectMapper()
                .configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false);
        seeder = new ProductDataSeeder(
                categoryRepository,
                taskMasterRepository,
                applicationRepository,
                categoryService,
                productCacheService,
                new LocationNormalizer(),
                objectMapper,
                new ClassPathResource("seed/categories.json"),
                new ClassPathResource("seed/taskMasters.json"));

        when(categoryService.normalizeId(anyString()))
                .thenAnswer(invocation -> invocation.getArgument(0));
        when(categoryService.normalizeAndValidateCategoryIds(any(Collection.class)))
                .thenAnswer(invocation -> new ArrayList<>(
                        (Collection<String>) invocation.getArgument(0)));
    }

    @Test
    public void seedIfEmpty_seedsCatalogBeforeValidatedTaskMasters() throws Exception {
        seeder.seedIfEmpty();

        verify(categoryService, times(36))
                .createCategory(anyString(), anyString(), anyString(), anyString());

        ArgumentCaptor<List<TaskMaster>> taskMastersCaptor = ArgumentCaptor.forClass(List.class);
        verify(taskMasterRepository).saveAll(taskMastersCaptor.capture());

        List<TaskMaster> taskMasters = taskMastersCaptor.getValue();
        assertThat(taskMasters).hasSize(18);
        assertThat(taskMasters)
                .allSatisfy(taskMaster -> {
                    assertThat(taskMaster.getLocationCity()).isNotBlank();
                    assertThat(taskMaster.getLocationStateCode()).hasSize(2);
                    assertThat(taskMaster.getJobCategories()).isNotEmpty();
                });
        verify(categoryService, times(18))
                .normalizeAndValidateCategoryIds(any(Collection.class));

        InOrder inOrder = inOrder(categoryService, productCacheService, taskMasterRepository);
        inOrder.verify(categoryService, times(36))
                .createCategory(anyString(), anyString(), anyString(), anyString());
        inOrder.verify(productCacheService).evictCategoryCatalog();
        inOrder.verify(taskMasterRepository).saveAll(any());
        inOrder.verify(productCacheService).evictOnCreate();
    }

    @Test
    public void seedIfEmpty_existingTaskMastersWithoutCatalog_failsInsteadOfMigrating() {
        when(taskMasterRepository.count()).thenReturn(1L);

        assertThatThrownBy(() -> seeder.seedIfEmpty())
                .isInstanceOf(IllegalStateException.class)
                .hasMessageContaining("Reset the products database");

        verify(categoryService, never())
                .createCategory(anyString(), anyString(), anyString(), anyString());
        verify(taskMasterRepository, never()).saveAll(any());
        verify(productCacheService, never()).evictCategoryCatalog();
    }

    @Test
    public void seedIfEmpty_existingApplicationsWithoutCatalog_failsInsteadOfDiscardingHistory() {
        when(applicationRepository.count()).thenReturn(1L);

        assertThatThrownBy(() -> seeder.seedIfEmpty())
                .isInstanceOf(IllegalStateException.class)
                .hasMessageContaining("Legacy product data exists");
    }

    @Test
    public void seedIfEmpty_isRepeatableForExistingCatalogAndTaskMasters() throws Exception {
        when(categoryRepository.count()).thenReturn(36L);
        when(categoryRepository.existsById(anyString())).thenReturn(true);
        when(taskMasterRepository.count()).thenReturn(18L);

        seeder.seedIfEmpty();

        verify(categoryService, never())
                .createCategory(anyString(), anyString(), anyString(), anyString());
        verify(taskMasterRepository, never()).saveAll(any());
        verify(productCacheService).evictCategoryCatalog();
        verify(productCacheService, never()).evictOnCreate();
    }

    @Test
    public void seedResources_haveUniqueCatalogEntriesAndNoUnknownTaskMasterCategories()
            throws Exception {
        ObjectMapper objectMapper = new ObjectMapper()
                .configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false);
        ProductDataSeeder.CategorySeedPayload categoryPayload;
        ProductDataSeeder.TaskMasterSeedPayload taskMasterPayload;

        try (InputStream inputStream =
                     new ClassPathResource("seed/categories.json").getInputStream()) {
            categoryPayload = objectMapper.readValue(
                    inputStream,
                    ProductDataSeeder.CategorySeedPayload.class);
        }
        try (InputStream inputStream =
                     new ClassPathResource("seed/taskMasters.json").getInputStream()) {
            taskMasterPayload = objectMapper.readValue(
                    inputStream,
                    ProductDataSeeder.TaskMasterSeedPayload.class);
        }

        Set<String> categoryIds = new HashSet<>();
        Set<String> normalizedDisplayNames = new HashSet<>();
        categoryPayload.categories.forEach(category -> {
            assertThat(categoryIds.add(category.id))
                    .as("duplicate category ID %s", category.id)
                    .isTrue();
            assertThat(normalizedDisplayNames.add(
                    category.displayName.trim().replaceAll("\\s+", " ").toLowerCase()))
                    .as("duplicate category display name %s", category.displayName)
                    .isTrue();
        });

        taskMasterPayload.taskMasters.forEach(taskMaster ->
                assertThat(categoryIds)
                        .contains(taskMaster.getJobCategories()));
    }
}
