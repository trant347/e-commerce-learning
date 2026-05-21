package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.messaging.ApplicationEventPublisher;
import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.model.TaskMasterApplication;
import com.bookstore.productsevice.model.TaskMasterApplication.ApplicationStatus;
import com.bookstore.productsevice.repository.ApplicationRepository;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;

import javax.servlet.http.HttpServletRequest;
import java.time.Instant;
import java.util.List;
import java.util.Map;

/**
 * Manages the TaskMaster application lifecycle:
 *   POST   /products/applications            – any authenticated user submits an application
 *   GET    /products/applications            – admin lists all applications (optionally filtered by status)
 *   GET    /products/applications/{id}       – admin views a specific application
 *   PUT    /products/applications/{id}/accept  – admin accepts → creates TaskMaster + notifies applicant
 *   PUT    /products/applications/{id}/decline – admin declines → notifies applicant
 *
 * All endpoints require a valid JWT (enforced by JwtTokenFilter on /products/*).
 * Admin-only endpoints additionally check for ROLE_ADMIN in the token authorities.
 */
@RestController
@RequestMapping("/products/applications")
public class ApplicationController {

    private static final Logger log = LoggerFactory.getLogger(ApplicationController.class);

    private final ApplicationRepository applicationRepository;
    private final TaskMasterRepository taskMasterRepository;
    private final ApplicationEventPublisher eventPublisher;

    public ApplicationController(ApplicationRepository applicationRepository,
                                 TaskMasterRepository taskMasterRepository,
                                 ApplicationEventPublisher eventPublisher) {
        this.applicationRepository = applicationRepository;
        this.taskMasterRepository = taskMasterRepository;
        this.eventPublisher = eventPublisher;
    }

    // -------------------------------------------------------------------------
    // Submit a new application (any authenticated user)
    // -------------------------------------------------------------------------

    @PostMapping
    public ResponseEntity<?> submitApplication(@RequestBody TaskMasterApplication body,
                                               HttpServletRequest request) {
        String username = getUsername(request);
        log.info("[ApplicationController] POST /products/applications user='{}'", username);

        // Reject if the user already has a pending application
        if (applicationRepository.findByApplicantUsernameAndStatus(username, ApplicationStatus.PENDING).isPresent()) {
            log.warn("[ApplicationController] User '{}' already has a PENDING application", username);
            return ResponseEntity.status(HttpStatus.CONFLICT)
                    .body(Map.of("error", "You already have a pending application."));
        }

        TaskMasterApplication application = new TaskMasterApplication()
                .setApplicantUsername(username)
                .setName(body.getName())
                .setAge(body.getAge())
                .setLocation(body.getLocation())
                .setDescription(body.getDescription())
                .setHourlyRateUsd(body.getHourlyRateUsd())
                .setPhoto(body.getPhoto())
                .setJobCategories(body.getJobCategories())
                .setStatus(ApplicationStatus.PENDING)
                .setSubmittedAt(Instant.now());

        TaskMasterApplication saved = applicationRepository.save(application);
        log.info("[ApplicationController] Saved application id='{}' for user='{}'", saved.getId(), username);

        eventPublisher.publishApplicationSubmitted(saved.getId(), username);

        return ResponseEntity.status(HttpStatus.CREATED).body(saved);
    }

    // -------------------------------------------------------------------------
    // List applications (admin only)
    // -------------------------------------------------------------------------

    @GetMapping
    public ResponseEntity<?> listApplications(@RequestParam(required = false) ApplicationStatus status,
                                              HttpServletRequest request) {
        if (!isAdmin(request)) return forbidden();

        List<TaskMasterApplication> results = status != null
                ? applicationRepository.findAllByStatus(status)
                : applicationRepository.findAll();

        log.info("[ApplicationController] GET /products/applications status={} → {} results",
                status, results.size());
        return ResponseEntity.ok(results);
    }

    // -------------------------------------------------------------------------
    // Get a specific application (admin only)
    // -------------------------------------------------------------------------

    @GetMapping("/{id}")
    public ResponseEntity<?> getApplication(@PathVariable String id, HttpServletRequest request) {
        if (!isAdmin(request)) return forbidden();

        return applicationRepository.findById(id)
                .<ResponseEntity<?>>map(app -> {
                    log.debug("[ApplicationController] GET /products/applications/{} found", id);
                    return ResponseEntity.ok(app);
                })
                .orElseGet(() -> {
                    log.warn("[ApplicationController] Application id='{}' not found", id);
                    return ResponseEntity.notFound().build();
                });
    }

    // -------------------------------------------------------------------------
    // Accept an application (admin only)
    // -------------------------------------------------------------------------

    @PutMapping("/{id}/accept")
    public ResponseEntity<?> acceptApplication(@PathVariable String id, HttpServletRequest request) {
        if (!isAdmin(request)) return forbidden();

        TaskMasterApplication application = applicationRepository.findById(id).orElse(null);
        if (application == null) return ResponseEntity.notFound().build();

        if (application.getStatus() != ApplicationStatus.PENDING) {
            return ResponseEntity.status(HttpStatus.CONFLICT)
                    .body(Map.of("error", "Application is not in PENDING status."));
        }

        // Create the TaskMaster profile
        TaskMaster taskMaster = new TaskMaster()
                .setName(application.getName())
                .setAge(application.getAge())
                .setLocation(application.getLocation())
                .setDescription(application.getDescription())
                .setHourlyRateUsd(application.getHourlyRateUsd())
                .setPhoto(application.getPhoto())
                .setJobCategories(application.getJobCategories())
                .setRating(0.0)
                .setOwnerUsername(application.getApplicantUsername());

        try {
            TaskMaster saved = taskMasterRepository.save(taskMaster);
            log.info("[ApplicationController] Created TaskMaster id='{}' for user='{}'",
                    saved.getId(), application.getApplicantUsername());

            // Update application status
            application.setStatus(ApplicationStatus.ACCEPTED)
                       .setCreatedTaskMasterId(saved.getId());
            applicationRepository.save(application);

            eventPublisher.publishApplicationAccepted(application.getApplicantUsername(), saved.getId());

            return ResponseEntity.ok(saved);

        } catch (DuplicateKeyException e) {
            log.warn("[ApplicationController] User '{}' already has a TaskMaster profile",
                    application.getApplicantUsername());
            return ResponseEntity.status(HttpStatus.CONFLICT)
                    .body(Map.of("error", "This user already has a TaskMaster profile."));
        }
    }

    // -------------------------------------------------------------------------
    // Decline an application (admin only)
    // -------------------------------------------------------------------------

    @PutMapping("/{id}/decline")
    public ResponseEntity<?> declineApplication(@PathVariable String id,
                                                @RequestBody(required = false) Map<String, String> body,
                                                HttpServletRequest request) {
        if (!isAdmin(request)) return forbidden();

        TaskMasterApplication application = applicationRepository.findById(id).orElse(null);
        if (application == null) return ResponseEntity.notFound().build();

        if (application.getStatus() != ApplicationStatus.PENDING) {
            return ResponseEntity.status(HttpStatus.CONFLICT)
                    .body(Map.of("error", "Application is not in PENDING status."));
        }

        String reason = body != null ? body.get("reason") : null;
        application.setStatus(ApplicationStatus.DECLINED).setDeclineReason(reason);
        applicationRepository.save(application);

        log.info("[ApplicationController] Declined application id='{}' for user='{}' reason='{}'",
                id, application.getApplicantUsername(), reason);

        eventPublisher.publishApplicationDeclined(application.getApplicantUsername(), reason);

        return ResponseEntity.ok(application);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private String getUsername(HttpServletRequest request) {
        Object attr = request.getAttribute("authenticatedUsername");
        return attr != null ? attr.toString() : "unknown";
    }

    @SuppressWarnings("unchecked")
    private boolean isAdmin(HttpServletRequest request) {
        Object attr = request.getAttribute("authenticatedAuthorities");
        if (attr instanceof List<?>) {
            List<?> authorities = (List<?>) attr;
            return authorities.stream()
                    .anyMatch(a -> "ROLE_ADMIN".equals(a.toString()));
        }
        return false;
    }

    private ResponseEntity<?> forbidden() {
        return ResponseEntity.status(HttpStatus.FORBIDDEN)
                .body(Map.of("error", "Admin access required."));
    }
}
