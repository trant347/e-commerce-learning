package com.bookstore.productsevice.repository;

import com.bookstore.productsevice.model.Book;
import org.bson.Document;
import org.bson.conversions.Bson;

import java.util.ArrayList;
import java.util.Optional;

public class BookMapper {
    public static Book mapBsonToBook(Bson bson) {
        Book book = new Book();

        Document document = (Document)bson;

        book.setPicture(document.getString("picture"));
        book.setName(document.getString("name"));
        ArrayList<String> authors = ((ArrayList<String>)document.get("authors"));
        Optional.ofNullable(authors).ifPresent((value) -> book.setAuthors(value.toArray(new String[value.size()])));
        book.setDescription(document.getString("description"));
        book.setPriceUsd(document.getDouble("priceUsd"));
        book.setRating(document.getDouble("rating"));

        return book;

    }
}
