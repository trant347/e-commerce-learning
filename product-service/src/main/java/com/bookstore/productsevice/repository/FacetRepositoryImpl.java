package com.bookstore.productsevice.repository;

import org.bson.Document;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.data.domain.Sort;
import org.springframework.data.mongodb.core.MongoOperations;
import org.springframework.data.mongodb.core.aggregation.Aggregation;
import org.springframework.data.mongodb.core.aggregation.AggregationResults;

import java.util.List;
import java.util.Optional;

import static org.springframework.data.mongodb.core.aggregation.Aggregation.bucket;
import static org.springframework.data.mongodb.core.aggregation.Aggregation.facet;
import static org.springframework.data.mongodb.core.aggregation.Aggregation.match;
import static org.springframework.data.mongodb.core.aggregation.Aggregation.newAggregation;
import static org.springframework.data.mongodb.core.aggregation.Aggregation.project;
import static org.springframework.data.mongodb.core.aggregation.Aggregation.skip;
import static org.springframework.data.mongodb.core.aggregation.Aggregation.sort;
import static org.springframework.data.mongodb.core.query.Criteria.where;



public class FacetRepositoryImpl implements FacetRepository {

    private MongoOperations mongoOperations;

    @Value(value = "book")
    private String collection;

    @Autowired
    public  FacetRepositoryImpl(MongoOperations mongoOperations) {
        this.mongoOperations = mongoOperations;
    }

    /**
     * This method is the java implementation of the following mongo shell aggregation pipeline
     * pipeline.aggregate([ {$match: {cast: {$in: ... }}}, {$sort: {rating: -1}},
     * {$skip: ... }, {$limit: ... }, {$facet:{ rating: {$bucket: ...}, priceUsd: {$bucket: ...},
     * books: {$addFields: ...}, }} ])
     *
     *
     * @param name
     * @param page : page number - start from 0
     * @param itemsPerPage
     * @return
     */
    @Override
    public Optional<Document> getBooksUsingNameFacetSearch(String name, int page, int itemsPerPage, String[] sortFields) {
        long skip = page*itemsPerPage;

        Aggregation aggregation = newAggregation(
                match(where("name").regex(".*"+name+".*","i")), //
                sort(Sort.Direction.ASC, sortFields),
                skip(skip),
                facet(
                      bucket("priceUsd")
                        .withBoundaries(PRICE_USD_RANGES)
                        .withDefaultBucket("Other")
                        .andOutput("name").push().as("name")
                        .andOutputCount().as("count")
                ).as(PRICE_BUCKETS_NAME)
                .and(
                        bucket("rating")
                                .withBoundaries(RATING_RANGES)
                                .withDefaultBucket("Other")
                                .andOutput("name").push().as("name")
                                .andOutputCount().as("count")
                ).as(RATING_BUCKETS_NAME)
                .and(
                    project("name","priceUsd","authors","rating")
                ).as("books")
        );

        AggregationResults<Document> results = mongoOperations.aggregate(aggregation, collection, Document.class);

        List<Document> facets = results.getMappedResults();


        if(facets.isEmpty()) {
            return Optional.empty();
        }


        return Optional.ofNullable(facets.get(0));

    }


}
