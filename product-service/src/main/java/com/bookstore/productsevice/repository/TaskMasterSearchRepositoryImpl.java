package com.bookstore.productsevice.repository;

import com.bookstore.productsevice.location.LocationSearchCriteria;
import com.bookstore.productsevice.model.TaskMaster;
import org.springframework.data.mongodb.core.MongoTemplate;
import org.springframework.data.mongodb.core.query.Criteria;
import org.springframework.data.mongodb.core.query.Query;
import org.springframework.stereotype.Repository;

import java.util.List;

@Repository
public class TaskMasterSearchRepositoryImpl implements TaskMasterSearchRepository {

    private final MongoTemplate mongoTemplate;

    public TaskMasterSearchRepositoryImpl(MongoTemplate mongoTemplate) {
        this.mongoTemplate = mongoTemplate;
    }

    @Override
    public List<TaskMaster> findByLocation(LocationSearchCriteria location, Integer limit) {
        Query query = new Query();
        addLocationCriteria(query, location);
        if (limit != null) {
            query.limit(limit);
        }
        return mongoTemplate.find(query, TaskMaster.class);
    }

    @Override
    public List<TaskMaster> searchWithFilters(String category, LocationSearchCriteria location,
                                               Double minRate, Double maxRate,
                                               Double minRating, int limit) {
        Query query = new Query();

        if (category != null && !category.isBlank()) {
            query.addCriteria(Criteria.where("jobCategories").is(category));
        }
        if (location != null) {
            addLocationCriteria(query, location);
        }
        if (minRate != null || maxRate != null) {
            Criteria rateCriteria = Criteria.where("hourlyRateUsd");
            if (minRate != null) rateCriteria = rateCriteria.gte(minRate);
            if (maxRate != null) rateCriteria = rateCriteria.lte(maxRate);
            query.addCriteria(rateCriteria);
        }
        if (minRating != null) {
            query.addCriteria(Criteria.where("rating").gte(minRating));
        }

        query.limit(limit);
        return mongoTemplate.find(query, TaskMaster.class);
    }

    private static void addLocationCriteria(Query query, LocationSearchCriteria location) {
        switch (location.matchMode()) {
            case CITY -> query.addCriteria(Criteria.where("locationCity").is(location.city()));
            case STATE -> query.addCriteria(Criteria.where("locationStateCode").is(location.stateCode()));
            case CITY_AND_STATE -> query.addCriteria(
                    Criteria.where("locationCity").is(location.city())
                            .and("locationStateCode").is(location.stateCode()));
            case CITY_OR_STATE -> query.addCriteria(new Criteria().orOperator(
                    Criteria.where("locationCity").is(location.city()),
                    Criteria.where("locationStateCode").is(location.stateCode())));
        }
    }
}
