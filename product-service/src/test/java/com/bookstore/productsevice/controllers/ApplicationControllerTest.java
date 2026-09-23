package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.category.CategoryService;
import com.bookstore.productsevice.category.CategoryValidationException;
import com.bookstore.productsevice.messaging.ApplicationEventPublisher;
import com.bookstore.productsevice.location.LocationNormalizer;
import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.model.TaskMasterApplication;
import com.bookstore.productsevice.model.TaskMasterApplication.ApplicationStatus;
import com.bookstore.productsevice.repository.ApplicationRepository;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.bookstore.productsevice.services.ProductCacheService;
import jakarta.servlet.http.HttpServletRequest;
import org.junit.Before;
import org.junit.Test;
import org.mockito.InOrder;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;

import java.util.Collections;
import java.util.List;
import java.util.Optional;
import java.util.Map;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.*;

/**
 * Verifies that accepting an application invalidates the product cache so the newly-created
 * TaskMaster appears in the next /products list call. Without this, the cached list of IDs
 * (5-minute TTL) keeps returning the stale page and the new profile is invisible until expiry.
 */
public class ApplicationControllerTest {

    private ApplicationRepository applicationRepository;
    private TaskMasterRepository taskMasterRepository;
    private ApplicationEventPublisher eventPublisher;
    private ProductCacheService productCacheService;
    private CategoryService categoryService;
    private HttpServletRequest request;

    private ApplicationController controller;

    @Before
    public void setUp() {
        applicationRepository = mock(ApplicationRepository.class);
        taskMasterRepository = mock(TaskMasterRepository.class);
        eventPublisher = mock(ApplicationEventPublisher.class);
        productCacheService = mock(ProductCacheService.class);
        categoryService = mock(CategoryService.class);
        request = mock(HttpServletRequest.class);
        when(categoryService.normalizeAndValidateCategoryIds(any(String[].class)))
                .thenAnswer(invocation -> invocation.getArgument(0));

        controller = new ApplicationController(
                applicationRepository,
                taskMasterRepository,
                eventPublisher,
                productCacheService,
                new LocationNormalizer(),
                categoryService);
    }

    @Test
    public void acceptApplication_evictsCacheAfterSavingTaskMaster() {
        givenAdmin();

        TaskMasterApplication pending = new TaskMasterApplication()
                .setApplicantUsername("panda")
                .setName("Panda")
                .setAge(30)
                .setLocation("Chicago, IL")
                .setDescription("Handy")
                .setHourlyRateUsd(25.0)
                .setPhoto("photo.png")
                .setJobCategories(new String[]{" Plumbing ", "PLUMBING"})
                .setStatus(ApplicationStatus.PENDING);
        pending.setId("app-1");

        when(applicationRepository.findById("app-1")).thenReturn(Optional.of(pending));
        when(categoryService.normalizeAndValidateCategoryIds(pending.getJobCategories()))
                .thenReturn(new String[]{"plumbing"});

        TaskMaster persisted = new TaskMaster();
        persisted.setId("tm-1");
        when(taskMasterRepository.save(any(TaskMaster.class))).thenReturn(persisted);
        when(applicationRepository.save(any(TaskMasterApplication.class)))
                .thenAnswer(inv -> inv.getArgument(0));

        ResponseEntity<?> response = controller.acceptApplication("app-1", request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);

        // Cache eviction must happen after the TaskMaster is saved, otherwise the list endpoint
        // can repopulate the cache from a snapshot taken before the insert.
        InOrder inOrder = inOrder(taskMasterRepository, productCacheService);
        inOrder.verify(taskMasterRepository).save(any(TaskMaster.class));
        inOrder.verify(productCacheService).evictOnCreate();

        verify(eventPublisher).publishApplicationAccepted("panda", "tm-1");
        verify(taskMasterRepository).save(argThat(taskMaster ->
                "Chicago, IL".equals(taskMaster.getLocation())
                        && "chicago".equals(taskMaster.getLocationCity())
                        && "IL".equals(taskMaster.getLocationStateCode())
                        && java.util.Arrays.equals(
                                new String[]{"plumbing"},
                                taskMaster.getJobCategories())));
        assertThat(pending.getStatus()).isEqualTo(ApplicationStatus.ACCEPTED);
        assertThat(pending.getCreatedTaskMasterId()).isEqualTo("tm-1");
        assertThat(pending.getJobCategories()).containsExactly("plumbing");
    }

    @Test
    public void acceptApplication_nonPendingApplication_doesNotTouchCacheOrCreateTaskMaster() {
        givenAdmin();

        TaskMasterApplication accepted = new TaskMasterApplication()
                .setApplicantUsername("panda")
                .setStatus(ApplicationStatus.ACCEPTED);
        accepted.setId("app-3");
        when(applicationRepository.findById("app-3")).thenReturn(Optional.of(accepted));

        ResponseEntity<?> response = controller.acceptApplication("app-3", request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.CONFLICT);
        verifyNoInteractions(taskMasterRepository);
        verifyNoInteractions(productCacheService);
        verifyNoInteractions(eventPublisher);
    }

    @Test
    public void acceptApplication_nonAdminCaller_isForbiddenAndCacheUntouched() {
        when(request.getAttribute("authenticatedAuthorities"))
                .thenReturn(Collections.singletonList("ROLE_USER"));

        ResponseEntity<?> response = controller.acceptApplication("app-x", request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.FORBIDDEN);
        verifyNoInteractions(applicationRepository);
        verifyNoInteractions(taskMasterRepository);
        verifyNoInteractions(productCacheService);
    }

    @Test
    public void submitApplication_normalizesAndDeduplicatesCategoriesBeforeSaving() {
        when(request.getAttribute("authenticatedUsername")).thenReturn("panda");
        when(categoryService.normalizeAndValidateCategoryIds(argThat((String[] categories) ->
                java.util.Arrays.equals(
                        categories,
                        new String[]{" Plumbing ", "PLUMBING"}))))
                .thenReturn(new String[]{"plumbing"});
        when(applicationRepository.save(any(TaskMasterApplication.class)))
                .thenAnswer(invocation -> {
                    TaskMasterApplication application = invocation.getArgument(0);
                    application.setId("app-1");
                    return application;
                });

        TaskMasterApplication body = new TaskMasterApplication()
                .setName("Panda")
                .setAge(30)
                .setLocation("Chicago, IL")
                .setDescription("Handy")
                .setHourlyRateUsd(25.0)
                .setJobCategories(new String[]{" Plumbing ", "PLUMBING"});

        ResponseEntity<?> response = controller.submitApplication(body, request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        verify(applicationRepository).save(argThat(application ->
                java.util.Arrays.equals(
                        new String[]{"plumbing"},
                        application.getJobCategories())));
        verify(eventPublisher).publishApplicationSubmitted("app-1", "panda");
    }

    @Test
    public void submitApplication_unknownCategoryDoesNotSaveOrPublishEvent() {
        when(request.getAttribute("authenticatedUsername")).thenReturn("panda");
        when(categoryService.normalizeAndValidateCategoryIds(argThat((String[] categories) ->
                java.util.Arrays.equals(
                        categories,
                        new String[]{"unknown-service"}))))
                .thenThrow(new CategoryValidationException(
                        "unknown_category",
                        "Unknown categories: unknown-service.",
                        Map.of("jobCategories", "unknown categories: unknown-service")));
        TaskMasterApplication body = new TaskMasterApplication()
                .setName("Panda")
                .setLocation("Chicago, IL")
                .setJobCategories(new String[]{"unknown-service"});

        assertThatThrownBy(() -> controller.submitApplication(body, request))
                .isInstanceOf(CategoryValidationException.class)
                .extracting("errorCode")
                .isEqualTo("unknown_category");

        verify(applicationRepository, never()).save(any(TaskMasterApplication.class));
        verify(eventPublisher, never()).publishApplicationSubmitted(any(), any());
    }

    @Test
    public void acceptApplication_unknownCategoryDoesNotPublishProfile() {
        givenAdmin();
        TaskMasterApplication pending = new TaskMasterApplication()
                .setApplicantUsername("panda")
                .setLocation("Chicago, IL")
                .setJobCategories(new String[]{"unknown-service"})
                .setStatus(ApplicationStatus.PENDING);
        pending.setId("app-2");
        when(applicationRepository.findById("app-2")).thenReturn(Optional.of(pending));
        when(categoryService.normalizeAndValidateCategoryIds(argThat((String[] categories) ->
                java.util.Arrays.equals(
                        categories,
                        new String[]{"unknown-service"}))))
                .thenThrow(new CategoryValidationException(
                        "unknown_category",
                        "Unknown categories: unknown-service.",
                        Map.of("jobCategories", "unknown categories: unknown-service")));

        assertThatThrownBy(() -> controller.acceptApplication("app-2", request))
                .isInstanceOf(CategoryValidationException.class)
                .extracting("errorCode")
                .isEqualTo("unknown_category");

        verifyNoInteractions(taskMasterRepository);
        verify(productCacheService, never()).evictOnCreate();
        verify(eventPublisher, never()).publishApplicationAccepted(any(), any());
        verify(applicationRepository, never()).save(any(TaskMasterApplication.class));
        assertThat(pending.getStatus()).isEqualTo(ApplicationStatus.PENDING);
    }

    private void givenAdmin() {
        List<String> authorities = Collections.singletonList("ROLE_ADMIN");
        when(request.getAttribute("authenticatedAuthorities")).thenReturn(authorities);
    }
}
