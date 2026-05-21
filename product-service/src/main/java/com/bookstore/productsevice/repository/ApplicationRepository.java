package com.bookstore.productsevice.repository;

import com.bookstore.productsevice.model.TaskMasterApplication;
import org.springframework.data.mongodb.repository.MongoRepository;

import java.util.List;
import java.util.Optional;

public interface ApplicationRepository extends MongoRepository<TaskMasterApplication, String> {

    /** Find all applications submitted by a specific user. */
    List<TaskMasterApplication> findAllByApplicantUsername(String applicantUsername);

    /** Check whether a user already has a pending application. */
    Optional<TaskMasterApplication> findByApplicantUsernameAndStatus(
            String applicantUsername,
            TaskMasterApplication.ApplicationStatus status);

    /** Fetch all applications in a given status (for admin listing). */
    List<TaskMasterApplication> findAllByStatus(TaskMasterApplication.ApplicationStatus status);
}
