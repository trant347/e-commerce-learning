package com.bookstore.productsevice.validators;


import com.bookstore.productsevice.exception.MissingParametersException;
import com.bookstore.productsevice.model.Book;

import java.util.ArrayList;
import java.util.List;

public class BookValidator {

    public static void validate(Book book) throws Exception {
        List<String> missingParams = new ArrayList<>();
        if(book.getName() == null) {
            missingParams.add("name");
        }

        if(book.getAuthors() == null || book.getAuthors().length == 0) {
            missingParams.add("authors");
        }

        if(book.getPriceUsd() == 0) {
            missingParams.add("price in USD");
        }

        if(missingParams.size() > 0) {
            throw new MissingParametersException(missingParams.toArray(new String[missingParams.size()]));
        }


    }
}
