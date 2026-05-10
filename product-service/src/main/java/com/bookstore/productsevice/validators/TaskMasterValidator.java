package com.bookstore.productsevice.validators;

import com.bookstore.productsevice.exception.MissingParametersException;
import com.bookstore.productsevice.model.TaskMaster;

import java.util.ArrayList;
import java.util.List;

public class TaskMasterValidator {

    public static void validate(TaskMaster taskMaster) throws Exception {
        List<String> missingParams = new ArrayList<>();

        if (taskMaster.getName() == null || taskMaster.getName().trim().isEmpty()) {
            missingParams.add("name");
        }

        if (taskMaster.getLocation() == null || taskMaster.getLocation().trim().isEmpty()) {
            missingParams.add("location");
        }

        if (taskMaster.getJobCategories() == null || taskMaster.getJobCategories().length == 0) {
            missingParams.add("jobCategories");
        }

        if (taskMaster.getDescription() == null || taskMaster.getDescription().trim().isEmpty()) {
            missingParams.add("description");
        }

        if (taskMaster.getHourlyRateUsd() <= 0) {
            missingParams.add("hourlyRateUsd");
        }

        if (!missingParams.isEmpty()) {
            throw new MissingParametersException(missingParams.toArray(new String[0]));
        }
    }
}
