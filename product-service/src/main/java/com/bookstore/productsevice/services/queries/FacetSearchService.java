package com.bookstore.productsevice.services.queries;

import com.bookstore.productsevice.model.Book;
import com.bookstore.productsevice.repository.BookMapper;
import com.bookstore.productsevice.repository.BookRepository;
import org.bson.Document;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.stereotype.Service;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.Map;

import static com.bookstore.productsevice.repository.FacetRepository.PRICE_BUCKETS_NAME;
import static com.bookstore.productsevice.repository.FacetRepository.PRICE_USD_RANGES;
import static com.bookstore.productsevice.repository.FacetRepository.RATING_BUCKETS_NAME;
import static com.bookstore.productsevice.repository.FacetRepository.RATING_RANGES;


@Service
public class FacetSearchService implements SearchService {

    private BookRepository facetRepository;


    @Autowired
    public FacetSearchService(BookRepository facetRepository) {
        this.facetRepository = facetRepository;
    }

    public Map<String, ?> getBooksByNameFacetSearch(String name, int page, int itemsPerPage, String[] sortFields) {

        Map<String, Object> results = new HashMap<>();
        Document document = facetRepository.getBooksUsingNameFacetSearch(name, page, itemsPerPage, sortFields).get();

        if(document == null) {
            return results;
        }


        ArrayList<Book> books = new ArrayList<>();

        ArrayList<Document> bookList = (ArrayList<Document>)document.get("books");

        if(bookList != null) {
            bookList.iterator().forEachRemaining(book -> books.add(BookMapper.mapBsonToBook(book)));
        }

        results.put("books", books);

        Document rating = new Document();
        rating.put("items", document.get(RATING_BUCKETS_NAME));
        rating.put("ranges", RATING_RANGES);
        results.put("rating", rating);

        Document priceUsd = new Document();
        priceUsd.put("items", document.get(PRICE_BUCKETS_NAME));
        priceUsd.put("ranges", PRICE_USD_RANGES);
        results.put("priceUsd", priceUsd);


        return results;



    }
}
