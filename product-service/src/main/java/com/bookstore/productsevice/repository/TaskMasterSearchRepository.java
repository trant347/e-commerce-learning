package com.bookstore.productsevice.repository;

import com.bookstore.productsevice.model.TaskMaster;

import java.util.List;

public interface TaskMasterSearchRepository {
    List<TaskMaster> searchWithFilters(String category, String location,
                                       Double minRate, Double maxRate,
                                       Double minRating, int limit);
}
