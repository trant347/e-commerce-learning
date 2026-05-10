package com.bookstore.productsevice.model;

import org.junit.Assert;
import org.junit.Test;

public class TaskMasterBuilderTest {

    @Test
    public void testBuilder() {
        TaskMaster taskMaster = new TaskMaster.Builder()
                .setName("John Smith")
                .setLocation("New York, NY")
                .setJobCategories(new String[]{"plumbing"})
                .setHourlyRateUsd(75.0)
                .build();
        Assert.assertEquals(taskMaster.getName(), "John Smith");
        Assert.assertEquals(taskMaster.getJobCategories()[0], "plumbing");
    }
}
