package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.category.CategoryService;
import com.bookstore.productsevice.category.CategoryValidationException;
import com.bookstore.productsevice.location.LocationNormalizer;
import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.bookstore.productsevice.services.ProductCacheService;
import org.junit.Before;
import org.junit.Test;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.test.util.ReflectionTestUtils;

import java.util.Map;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

public class TaskMasterCategoryValidationTest {

    private TaskMasterRepository taskMasterRepository;
    private ProductCacheService productCacheService;
    private CategoryService categoryService;
    private TaskMasterController controller;

    @Before
    public void setUp() {
        taskMasterRepository = mock(TaskMasterRepository.class);
        productCacheService = mock(ProductCacheService.class);
        categoryService = mock(CategoryService.class);
        controller = new TaskMasterController();
        controller.taskMasterRepository = taskMasterRepository;
        ReflectionTestUtils.setField(controller, "productCacheService", productCacheService);
        ReflectionTestUtils.setField(controller, "locationNormalizer", new LocationNormalizer());
        ReflectionTestUtils.setField(controller, "categoryService", categoryService);
    }

    @Test
    public void createTaskMaster_persistsOnlyCanonicalDeduplicatedCategories()
            throws Exception {
        TaskMaster input = validTaskMaster()
                .setJobCategories(new String[]{" Plumbing ", "PLUMBING"});
        when(categoryService.normalizeAndValidateCategoryIds(input.getJobCategories()))
                .thenReturn(new String[]{"plumbing"});
        when(taskMasterRepository.save(any(TaskMaster.class)))
                .thenAnswer(invocation -> invocation.getArgument(0));

        ResponseEntity<TaskMaster> response = controller.createTaskMaster(input);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(response.getBody().getJobCategories()).containsExactly("plumbing");
        verify(taskMasterRepository).save(input);
        verify(productCacheService).evictOnCreate();
    }

    @Test
    public void createTaskMaster_unknownCategoryIsRejectedBeforeSaving() {
        TaskMaster input = validTaskMaster()
                .setJobCategories(new String[]{"unknown-service"});
        when(categoryService.normalizeAndValidateCategoryIds(input.getJobCategories()))
                .thenThrow(new CategoryValidationException(
                        "unknown_category",
                        "Unknown categories: unknown-service.",
                        Map.of("jobCategories", "unknown categories: unknown-service")));

        assertThatThrownBy(() -> controller.createTaskMaster(input))
                .isInstanceOf(CategoryValidationException.class)
                .extracting("errorCode")
                .isEqualTo("unknown_category");

        verify(taskMasterRepository, never()).save(any());
        verify(productCacheService, never()).evictOnCreate();
    }

    @Test
    public void createTaskMaster_emptyCategoriesUseCatalogValidationPath() {
        TaskMaster input = validTaskMaster().setJobCategories(new String[0]);
        when(categoryService.normalizeAndValidateCategoryIds(input.getJobCategories()))
                .thenThrow(new CategoryValidationException(
                        "invalid_category",
                        "At least one category is required.",
                        Map.of("jobCategories", "must contain at least one category")));

        assertThatThrownBy(() -> controller.createTaskMaster(input))
                .isInstanceOf(CategoryValidationException.class)
                .extracting("errorCode")
                .isEqualTo("invalid_category");

        verify(taskMasterRepository, never()).save(any());
    }

    private TaskMaster validTaskMaster() {
        return new TaskMaster()
                .setName("Panda")
                .setAge(30)
                .setLocation("Chicago, IL")
                .setDescription("Handy")
                .setHourlyRateUsd(25.0)
                .setOwnerUsername("panda");
    }
}
