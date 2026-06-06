package com.bookstore.productsevice.repository;

import com.bookstore.productsevice.model.TaskMaster;
import org.springframework.data.domain.Pageable;
import org.springframework.data.mongodb.repository.MongoRepository;

import java.util.List;
import java.util.Optional;

public interface TaskMasterRepository extends MongoRepository<TaskMaster, String>, FacetRepository, TaskMasterSearchRepository {
    List<TaskMaster> findAllByName(String name);
    List<TaskMaster> findAllByLocation(String location);
    List<TaskMaster> findAllByJobCategoriesContaining(String category);
    List<TaskMaster> findTaskMasterByHourlyRateUsdBetween(double low, double high);
    List<TaskMaster> findTaskMasterByRatingGreaterThanEqual(double rating);
    long deleteByOwnerUsername(String ownerUsername);
    Optional<TaskMaster> findByOwnerUsername(String ownerUsername);

    // Pageable overloads for limited queries (used by MCP tools)
    List<TaskMaster> findAllByLocation(String location, Pageable pageable);
    List<TaskMaster> findAllByJobCategoriesContaining(String category, Pageable pageable);
    List<TaskMaster> findTaskMasterByHourlyRateUsdBetween(double low, double high, Pageable pageable);
    List<TaskMaster> findTaskMasterByRatingGreaterThanEqual(double rating, Pageable pageable);
}
