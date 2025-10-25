package com.bookstore.productsevice.repository;

import org.bson.Document;

import java.util.List;
import java.util.Optional;

public interface FacetRepository  {

    public static final String RATING_BUCKETS_NAME = "ratingBuckets";
    public static final String PRICE_BUCKETS_NAME = "priceBuckets";
    public static final Double[] RATING_RANGES = new Double[] {0.0,5.0,7.0,8.5,10.0};
    public static final Double[] PRICE_USD_RANGES = new Double[] {0.0,25.0,50.0,75.0,100.0};

    public Optional<Document> getBooksUsingNameFacetSearch(String name, int page, int itemsPerPage, String[] sortFields);


}
