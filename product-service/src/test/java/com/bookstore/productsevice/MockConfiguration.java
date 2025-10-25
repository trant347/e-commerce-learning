package com.bookstore.productsevice;

import com.bookstore.productsevice.model.Book;
import com.bookstore.productsevice.repository.BookRepository;
import com.bookstore.productsevice.repository.FacetRepository;
import org.bson.Document;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
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

@Profile("unit_test")
@Configuration
public class MockConfiguration {

    Logger logger = LoggerFactory.getLogger(MockConfiguration.class);

    @Bean
    public BookRepository getFacetRepository() {

        logger.info(" Creating mock BookRepository ");

        return new BookRepository() {
            @Override
            public List<Book> findAllByName(String name) {
                return null;
            }

            @Override
            public List<Book> findAllByAuthors(String[] authors) {
                return null;
            }

            @Override
            public List<Book> findBookByPriceUsdBetween(double low, double high) {
                return null;
            }

            @Override
            public Optional<Document> getBooksUsingNameFacetSearch(String name, int page, int itemsPerPage, String[] sortFields) {
                Document document = new Document();

                ArrayList<Document> bookList = new ArrayList<>();
                Document book = new Document();
                book.put("name","test");
                book.put("priceUsd", 29.4);
                book.put("rating",2.0);
                bookList.add(book);
                document.put("books", bookList);


                ArrayList<Document> ratingList = new ArrayList<>();
                Document rating = new Document();
                rating.put("_id",0);
                rating.put("name", new String[] { "test" });
                rating.put("count",1);
                ratingList.add(rating);
                document.put("rating",ratingList);

                ArrayList<Document> priceList = new ArrayList<>();
                Document price = new Document();
                price.put("_id",25);
                price.put("name", new String[] { "test" });
                price.put("count",1);
                priceList.add(rating);
                document.put("rating",priceList);

                return Optional.of(document);
            }

            @Override
            public <S extends Book> List<S> saveAll(Iterable<S> iterable) {
                return null;
            }

            @Override
            public List<Book> findAll() {
                return null;
            }

            @Override
            public List<Book> findAll(Sort sort) {
                return null;
            }

            @Override
            public <S extends Book> S insert(S s) {
                return null;
            }

            @Override
            public <S extends Book> List<S> insert(Iterable<S> iterable) {
                return null;
            }

            @Override
            public <S extends Book> List<S> findAll(Example<S> example) {
                return null;
            }

            @Override
            public <S extends Book> List<S> findAll(Example<S> example, Sort sort) {
                return null;
            }

            @Override
            public Page<Book> findAll(Pageable pageable) {
                return null;
            }

            @Override
            public <S extends Book> S save(S s) {
                return null;
            }

            @Override
            public Optional<Book> findById(String s) {
                return Optional.empty();
            }

            @Override
            public boolean existsById(String s) {
                return false;
            }

            @Override
            public Iterable<Book> findAllById(Iterable<String> iterable) {
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
            public void delete(Book book) {

            }

            @Override
            public void deleteAll(Iterable<? extends Book> iterable) {

            }

            @Override
            public void deleteAll() {

            }

            @Override
            public <S extends Book> Optional<S> findOne(Example<S> example) {
                return Optional.empty();
            }

            @Override
            public <S extends Book> Page<S> findAll(Example<S> example, Pageable pageable) {
                return null;
            }

            @Override
            public <S extends Book> long count(Example<S> example) {
                return 0;
            }

            @Override
            public <S extends Book> boolean exists(Example<S> example) {
                return false;
            }
        };
    }
}
