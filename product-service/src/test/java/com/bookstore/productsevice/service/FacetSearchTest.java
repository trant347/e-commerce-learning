package com.bookstore.productsevice.service;

import com.bookstore.productsevice.MockConfiguration;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.bookstore.productsevice.services.queries.TaskMasterSearchService;
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
    TaskMasterRepository taskMasterRepository;

    @Test
    public void testNameFacetSearch() {
        TaskMasterSearchService searchService = new TaskMasterSearchService(taskMasterRepository);
        Map<String,?> map = searchService.getTaskMastersByNameFacetSearch("test", 0, 10, new String[] { "hourlyRateUsd" });

        assertEquals(((ArrayList<Document>)map.get("taskMasters")).size(), 1);
        assertNotNull(map.get("hourlyRateUsd"));
        assertNotNull(map.get("rating"));
    }
}
