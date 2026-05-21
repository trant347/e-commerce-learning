package com.bookstore.productsevice.model;

import lombok.Data;
import lombok.experimental.Accessors;
import org.springframework.data.annotation.Id;
import org.springframework.data.mongodb.core.index.Indexed;
import org.springframework.data.mongodb.core.mapping.Document;

import java.time.Instant;

/**
 * Represents a user's application to become a task master.
 * Submitted by any authenticated user; reviewed and actioned by admin.
 * On ACCEPTED, a TaskMaster document is created and this application is
 * linked to it via the createdTaskMasterId field.
 */
@Document(collection = "taskmaster_applications")
@Data
@Accessors(chain = true)
public class TaskMasterApplication {

    @Id
    private String id;

    /** Username of the user who submitted this application. Indexed for quick lookup. */
    @Indexed
    private String applicantUsername;

    // --- Profile fields (mirrors TaskMaster) ---
    private String name;
    private int age;
    private String location;
    private String description;
    private double hourlyRateUsd;
    private String photo;
    private String[] jobCategories;

    /** Current review state. */
    private ApplicationStatus status;

    /** When the application was submitted. */
    private Instant submittedAt;

    /** Set by admin when declining, so the applicant knows why. */
    private String declineReason;

    /**
     * The ID of the TaskMaster document created when this application was accepted.
     * Null until status = ACCEPTED.
     */
    private String createdTaskMasterId;

    public enum ApplicationStatus {
        PENDING,
        ACCEPTED,
        DECLINED
    }
}
