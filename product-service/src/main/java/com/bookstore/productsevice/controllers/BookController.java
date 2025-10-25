package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.exception.ItemNotFoundException;
import com.bookstore.productsevice.model.Book;
import com.bookstore.productsevice.repository.BookRepository;
import com.bookstore.productsevice.services.queries.FacetSearchService;
import com.bookstore.productsevice.storage.StorageService;
import com.bookstore.productsevice.validators.BookValidator;
import org.apache.http.HttpResponse;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import java.util.HashMap;
import java.util.List;
import java.util.Map;

@RestController
public class BookController {

    private static final int ITEM_PER_PAGE = 20;

    @Autowired
    public BookRepository bookRepository;

    @Autowired
    public StorageService storageService;

    @Autowired
    private FacetSearchService facetSearchService;

    @GetMapping(value = "/products", params = "name")
    public ResponseEntity<List<Book>> getBooksByName(@RequestParam String name) {
        List<Book> books = bookRepository.findAllByName(name);
        if(books.isEmpty()) {
            throw new ItemNotFoundException(name);
        }
        return new ResponseEntity<>(books, HttpStatus.OK);
    }

    @GetMapping("/products")
    public ResponseEntity<List<Book>> getBooks(@RequestParam (required = false) Integer page, @RequestParam (required = false) Integer limit) {
        //TODO: add pagination
        List<Book> books = bookRepository.findAll();
        return new ResponseEntity<>(books, HttpStatus.OK);
    }

    @PostMapping("/products/tests")
    public ResponseEntity<Void> saveBooks(@RequestBody  List<Book> books){
        return new ResponseEntity<>(HttpStatus.OK);
    }


    @PostMapping("/products")
    public ResponseEntity<Book> createBook(@RequestBody Book book) throws Exception {
        BookValidator.validate(book);
        Book response = bookRepository.save(book);
        return new ResponseEntity<>(response,HttpStatus.OK);
    }

    @GetMapping("/products/{id}")
    public ResponseEntity<Book> getBookById(@PathVariable String id) {
        Book book = bookRepository.findById(id).orElseThrow(() -> new ItemNotFoundException(id));
        return new ResponseEntity<>(book, HttpStatus.OK);
    }

    @GetMapping("/products/facet-search")
    public ResponseEntity<?> getBooksWithFacet(@RequestParam String name,
                                               @RequestParam (required = false) Integer page,
                                               @RequestParam (required = false) String[] sortedFields) {

        if(page == null) {
            page = 0;
        }
        if(sortedFields == null) {
            sortedFields = new String[] { "rating", "priceUsd" };
        }
        Map<String, ?> results = facetSearchService.getBooksByNameFacetSearch(name, page, ITEM_PER_PAGE, sortedFields);

        if(results.get("books") == null) {
            return ResponseEntity.notFound().build();
        }

        Map<String, Object> facets = new HashMap<>();
        facets.put("priceUsd", results.get("priceUsd"));
        facets.put("rating", results.get("rating"));

        HashMap<String, Object> response = new HashMap<>();
        response.put("books", results.get("books"));
        response.put("facets", facets);
        response.put("page", page);
        return ResponseEntity.ok(response);
    }

}
