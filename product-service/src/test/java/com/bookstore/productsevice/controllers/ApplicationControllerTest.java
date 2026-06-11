package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.messaging.ApplicationEventPublisher;
import com.bookstore.productsevice.messaging.CategoryEventPublisher;
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

import static org.assertj.core.api.Assertions.assertThat;
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
    private CategoryEventPublisher categoryEventPublisher;
    private HttpServletRequest request;

    private ApplicationController controller;

    @Before
    public void setUp() {
        applicationRepository = mock(ApplicationRepository.class);
        taskMasterRepository = mock(TaskMasterRepository.class);
        eventPublisher = mock(ApplicationEventPublisher.class);
        productCacheService = mock(ProductCacheService.class);
        categoryEventPublisher = mock(CategoryEventPublisher.class);
        request = mock(HttpServletRequest.class);

        controller = new ApplicationController(
                applicationRepository,
                taskMasterRepository,
                eventPublisher,
                productCacheService,
                categoryEventPublisher);
    }

    @Test
    public void acceptApplication_evictsCacheAndPublishesEventsAfterSavingTaskMaster() {
        givenAdmin();

        TaskMasterApplication pending = new TaskMasterApplication()
                .setApplicantUsername("panda")
                .setName("Panda")
                .setAge(30)
                .setLocation("Hanoi")
                .setDescription("Handy")
                .setHourlyRateUsd(25.0)
                .setPhoto("photo.png")
                .setJobCategories(new String[]{"plumbing"})
                .setStatus(ApplicationStatus.PENDING);
        pending.setId("app-1");

        when(applicationRepository.findById("app-1")).thenReturn(Optional.of(pending));

        TaskMaster persisted = new TaskMaster();
        persisted.setId("tm-1");
        when(taskMasterRepository.save(any(TaskMaster.class))).thenReturn(persisted);
        when(applicationRepository.save(any(TaskMasterApplication.class)))
                .thenAnswer(inv -> inv.getArgument(0));

        ResponseEntity<?> response = controller.acceptApplication("app-1", request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);

        // Cache eviction must happen after the TaskMaster is saved, otherwise the list endpoint
        // can repopulate the cache from a snapshot taken before the insert.
        InOrder inOrder = inOrder(taskMasterRepository, productCacheService, categoryEventPublisher);
        inOrder.verify(taskMasterRepository).save(any(TaskMaster.class));
        inOrder.verify(productCacheService).evictOnCreate();
        inOrder.verify(categoryEventPublisher).publishCategoriesUpdated();

        verify(eventPublisher).publishApplicationAccepted("panda", "tm-1");
        assertThat(pending.getStatus()).isEqualTo(ApplicationStatus.ACCEPTED);
        assertThat(pending.getCreatedTaskMasterId()).isEqualTo("tm-1");
    }

    @Test
    public void acceptApplication_kafkaFailureDoesNotBlockResponseButCacheStillEvicted() {
        givenAdmin();

        TaskMasterApplication pending = new TaskMasterApplication()
                .setApplicantUsername("panda")
                .setStatus(ApplicationStatus.PENDING);
        pending.setId("app-2");
        when(applicationRepository.findById("app-2")).thenReturn(Optional.of(pending));

        TaskMaster persisted = new TaskMaster();
        persisted.setId("tm-2");
        when(taskMasterRepository.save(any(TaskMaster.class))).thenReturn(persisted);
        when(applicationRepository.save(any(TaskMasterApplication.class)))
                .thenAnswer(inv -> inv.getArgument(0));
        doThrow(new RuntimeException("kafka down"))
                .when(categoryEventPublisher).publishCategoriesUpdated();

        ResponseEntity<?> response = controller.acceptApplication("app-2", request);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
        verify(productCacheService).evictOnCreate();
        verify(eventPublisher).publishApplicationAccepted("panda", "tm-2");
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
        verifyNoInteractions(categoryEventPublisher);
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
        verifyNoInteractions(categoryEventPublisher);
    }

    private void givenAdmin() {
        List<String> authorities = Collections.singletonList("ROLE_ADMIN");
        when(request.getAttribute("authenticatedAuthorities")).thenReturn(authorities);
    }
}
