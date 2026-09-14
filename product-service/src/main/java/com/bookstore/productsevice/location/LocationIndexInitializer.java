package com.bookstore.productsevice.location;

import com.bookstore.productsevice.model.TaskMaster;
import org.springframework.boot.CommandLineRunner;
import org.springframework.core.Ordered;
import org.springframework.core.annotation.Order;
import org.springframework.data.domain.Sort;
import org.springframework.data.mongodb.core.MongoTemplate;
import org.springframework.data.mongodb.core.index.Index;
import org.springframework.data.mongodb.core.query.Criteria;
import org.springframework.data.mongodb.core.query.Query;
import org.springframework.stereotype.Component;

@Component
@Order(Ordered.HIGHEST_PRECEDENCE)
public class LocationIndexInitializer implements CommandLineRunner {

    private final MongoTemplate mongoTemplate;

    public LocationIndexInitializer(MongoTemplate mongoTemplate) {
        this.mongoTemplate = mongoTemplate;
    }

    @Override
    public void run(String... args) {
        failIfLegacyDocumentsExist();

        var indexOps = mongoTemplate.indexOps(TaskMaster.class);
        indexOps.ensureIndex(new Index()
                .on("locationStateCode", Sort.Direction.ASC)
                .named("location_state_idx"));
        indexOps.ensureIndex(new Index()
                .on("locationCity", Sort.Direction.ASC)
                .on("locationStateCode", Sort.Direction.ASC)
                .named("location_city_state_idx"));
        indexOps.ensureIndex(new Index()
                .on("jobCategories", Sort.Direction.ASC)
                .on("locationStateCode", Sort.Direction.ASC)
                .named("category_state_idx"));
        indexOps.ensureIndex(new Index()
                .on("jobCategories", Sort.Direction.ASC)
                .on("locationCity", Sort.Direction.ASC)
                .on("locationStateCode", Sort.Direction.ASC)
                .named("category_city_state_idx"));
    }

    private void failIfLegacyDocumentsExist() {
        if (!mongoTemplate.collectionExists(TaskMaster.class)) {
            return;
        }

        Query missingNormalizedLocation = new Query(new Criteria().orOperator(
                Criteria.where("locationCity").exists(false),
                Criteria.where("locationCity").is(null),
                Criteria.where("locationStateCode").exists(false),
                Criteria.where("locationStateCode").is(null)));
        long legacyCount = mongoTemplate.count(missingNormalizedLocation, TaskMaster.class);
        if (legacyCount > 0) {
            throw new IllegalStateException(
                    "Found " + legacyCount + " TaskMaster document(s) without normalized location fields. "
                            + "Recreate the products database before starting product-service.");
        }
    }
}
