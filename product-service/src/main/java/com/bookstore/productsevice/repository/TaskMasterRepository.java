package com.bookstore.productsevice.repository;

import com.bookstore.productsevice.model.TaskMaster;
import org.springframework.data.mongodb.repository.MongoRepository;

import java.util.List;

public interface TaskMasterRepository extends MongoRepository<TaskMaster, String>, FacetRepository {
    List<TaskMaster> findAllByName(String name);
    List<TaskMaster> findAllByLocation(String location);
    List<TaskMaster> findAllByJobCategoriesContaining(String category);
    List<TaskMaster> findTaskMasterByHourlyRateUsdBetween(double low, double high);
    List<TaskMaster> findTaskMasterByRatingGreaterThanEqual(double rating);
    long deleteByOwnerUsername(String ownerUsername);
}
