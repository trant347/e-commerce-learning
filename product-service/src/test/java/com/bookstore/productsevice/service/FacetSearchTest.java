package com.bookstore.productsevice.service;

import com.bookstore.productsevice.MockConfiguration;
import com.bookstore.productsevice.repository.BookRepository;
import com.bookstore.productsevice.repository.FacetRepository;
import com.bookstore.productsevice.services.queries.FacetSearchService;
import org.bson.Document;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.context.annotation.Profile;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.context.TestPropertySource;
import org.springframework.test.context.junit4.SpringJUnit4ClassRunner;

import java.util.ArrayList;
import java.util.Map;

import static junit.framework.TestCase.assertNotNull;
import static org.junit.Assert.assertEquals;

@ActiveProfiles("unit_test")
@SpringBootTest(classes = MockConfiguration.class)
@TestPropertySource(properties={
        "spring.cloud.consul.enabled=false", "spring.cloud.consul.config.enabled=false","spring.cloud.consul.binder.enabled=false"
})
@RunWith(SpringJUnit4ClassRunner.class)
public class FacetSearchTest {

    @Autowired
    BookRepository facetRepository;

    @Test
    public void testNameFacetSearch() {
        FacetSearchService facetSearchService = new FacetSearchService(facetRepository);
        Map<String,?> map = facetSearchService.getBooksByNameFacetSearch("test", 0, 10, new String[] { "priceUsd" });

        assertEquals(((ArrayList<Document>)map.get("books")).size(),1);
        assertNotNull(map.get("priceUsd"));
        assertNotNull(map.get("rating"));
    }
}
