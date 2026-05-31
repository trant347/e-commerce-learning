package com.bookstore.productsevice;

import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.bookstore.productsevice.repository.FacetRepository;
import org.bson.Document;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.context.annotation.Profile;
import org.springframework.data.domain.Example;
import org.springframework.data.domain.Page;
import org.springframework.data.domain.Pageable;
import org.springframework.data.domain.Sort;

import java.util.ArrayList;
import java.util.List;
import java.util.Optional;
import java.util.function.Function;
import org.springframework.data.repository.query.FluentQuery;

@Profile("unit_test")
@Configuration
public class MockConfiguration {

    Logger logger = LoggerFactory.getLogger(MockConfiguration.class);

    @Bean
    public TaskMasterRepository getFacetRepository() {

        logger.info(" Creating mock TaskMasterRepository ");

        return new TaskMasterRepository() {
            @Override
            public List<TaskMaster> findAllByName(String name) {
                return null;
            }

            @Override
            public List<TaskMaster> findAllByLocation(String location) {
                return null;
            }

            @Override
            public List<TaskMaster> findAllByJobCategoriesContaining(String category) {
                return null;
            }

            @Override
            public List<TaskMaster> findTaskMasterByHourlyRateUsdBetween(double low, double high) {
                return null;
            }

            @Override
            public List<TaskMaster> findTaskMasterByRatingGreaterThanEqual(double rating) {
                return null;
            }

            @Override
            public long deleteByOwnerUsername(String ownerUsername) {
                return 0;
            }

            @Override
            public Optional<TaskMaster> findByOwnerUsername(String ownerUsername) {
                return Optional.empty();
            }

            @Override
            public Optional<Document> getBooksUsingNameFacetSearch(String name, int page, int itemsPerPage, String[] sortFields) {
                return Optional.empty();
            }

            @Override
            public Optional<Document> getTaskMastersUsingNameFacetSearch(String name, int page, int itemsPerPage, String[] sortFields) {
                Document document = new Document();

                ArrayList<Document> taskMasterList = new ArrayList<>();
                Document tm = new Document();
                tm.put("name", "test");
                tm.put("hourlyRateUsd", 50.0);
                tm.put("rating", 4.5);
                tm.put("location", "New York, NY");
                taskMasterList.add(tm);
                document.put("taskMasters", taskMasterList);

                ArrayList<Document> ratingList = new ArrayList<>();
                Document rating = new Document();
                rating.put("_id", 0);
                rating.put("name", new String[]{"test"});
                rating.put("count", 1);
                ratingList.add(rating);
                document.put("ratingBuckets", ratingList);

                ArrayList<Document> rateList = new ArrayList<>();
                Document rate = new Document();
                rate.put("_id", 25);
                rate.put("name", new String[]{"test"});
                rate.put("count", 1);
                rateList.add(rate);
                document.put("hourlyRateBuckets", rateList);

                return Optional.of(document);
            }

            @Override
            public <S extends TaskMaster> List<S> saveAll(Iterable<S> iterable) {
                return null;
            }

            @Override
            public List<TaskMaster> findAll() {
                return null;
            }

            @Override
            public List<TaskMaster> findAll(Sort sort) {
                return null;
            }

            @Override
            public <S extends TaskMaster> S insert(S s) {
                return null;
            }

            @Override
            public <S extends TaskMaster> List<S> insert(Iterable<S> iterable) {
                return null;
            }

            @Override
            public <S extends TaskMaster> List<S> findAll(Example<S> example) {
                return null;
            }

            @Override
            public <S extends TaskMaster> List<S> findAll(Example<S> example, Sort sort) {
                return null;
            }

            @Override
            public Page<TaskMaster> findAll(Pageable pageable) {
                return null;
            }

            @Override
            public <S extends TaskMaster> S save(S s) {
                return null;
            }

            @Override
            public Optional<TaskMaster> findById(String s) {
                return Optional.empty();
            }

            @Override
            public boolean existsById(String s) {
                return false;
            }

            @Override
            public List<TaskMaster> findAllById(Iterable<String> iterable) {
                return null;
            }

            @Override
            public long count() {
                return 0;
            }

            @Override
            public void deleteById(String s) {

            }

            @Override
            public void delete(TaskMaster taskMaster) {

            }

            @Override
            public void deleteAll(Iterable<? extends TaskMaster> iterable) {

            }

            @Override
            public void deleteAllById(Iterable<? extends String> ids) {

            }

            @Override
            public void deleteAll() {

            }

            @Override
            public <S extends TaskMaster> Optional<S> findOne(Example<S> example) {
                return Optional.empty();
            }

            @Override
            public <S extends TaskMaster> Page<S> findAll(Example<S> example, Pageable pageable) {
                return null;
            }

            @Override
            public <S extends TaskMaster> long count(Example<S> example) {
                return 0;
            }

            @Override
            public <S extends TaskMaster> boolean exists(Example<S> example) {
                return false;
            }

            @Override
            public <S extends TaskMaster, R> R findBy(Example<S> example, Function<FluentQuery.FetchableFluentQuery<S>, R> queryFunction) {
                return null;
            }
        };
    }
}
