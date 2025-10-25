package com.bookstore.productsevice.model;

import org.junit.Assert;
import org.junit.Test;

public class BookBuilderTest {

    @Test
    public void testBuilder() {
        Book book = new Book.Builder().setName("Test").setAuthors(new String[] {"John"}).setCategories(new String[]{"fiction"}).build();
        Assert.assertEquals(book.getName(),"Test");
        Assert.assertEquals(book.getAuthors()[0],"John");
    }
}
