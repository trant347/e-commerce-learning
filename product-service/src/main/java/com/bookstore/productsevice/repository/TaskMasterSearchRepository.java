package com.bookstore.productsevice.repository;

import com.bookstore.productsevice.location.LocationSearchCriteria;
import com.bookstore.productsevice.model.TaskMaster;

import java.util.List;

public interface TaskMasterSearchRepository {
    List<TaskMaster> findByLocation(LocationSearchCriteria location, Integer limit);

    List<TaskMaster> searchWithFilters(String category, LocationSearchCriteria location,
                                       Double minRate, Double maxRate,
                                       Double minRating, int limit);
}
