package com.bookstore.productsevice.repository;

import com.bookstore.productsevice.model.Category;
import org.springframework.data.mongodb.repository.MongoRepository;

import java.util.List;

public interface CategoryRepository extends MongoRepository<Category, String> {

    boolean existsByNormalizedDisplayName(String normalizedDisplayName);

    List<Category> findAllByOrderByDisplayNameAsc();
}
