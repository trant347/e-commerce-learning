package com.bookstore.productsevice.category;

import com.bookstore.productsevice.model.Category;
import org.springframework.boot.CommandLineRunner;
import org.springframework.data.domain.Sort;
import org.springframework.data.mongodb.core.MongoTemplate;
import org.springframework.data.mongodb.core.index.Index;
import org.springframework.stereotype.Component;

@Component
public class CategoryIndexInitializer implements CommandLineRunner {

    private final MongoTemplate mongoTemplate;

    public CategoryIndexInitializer(MongoTemplate mongoTemplate) {
        this.mongoTemplate = mongoTemplate;
    }

    @Override
    public void run(String... args) {
        mongoTemplate.indexOps(Category.class).ensureIndex(new Index()
                .on("normalizedDisplayName", Sort.Direction.ASC)
                .unique()
                .named("category_display_name_unique"));
    }
}
