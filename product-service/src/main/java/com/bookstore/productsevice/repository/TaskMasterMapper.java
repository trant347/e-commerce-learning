package com.bookstore.productsevice.repository;

import com.bookstore.productsevice.model.TaskMaster;
import org.bson.Document;
import org.bson.conversions.Bson;

import java.util.ArrayList;
import java.util.Optional;

public class TaskMasterMapper {
    public static TaskMaster mapBsonToTaskMaster(Bson bson) {
        TaskMaster taskMaster = new TaskMaster();

        Document document = (Document) bson;

        taskMaster.setId(document.getObjectId("_id") != null ? document.getObjectId("_id").toString() : document.getString("id"));
        taskMaster.setName(document.getString("name"));
        taskMaster.setAge(document.getInteger("age", 0));
        taskMaster.setPhoto(document.getString("photo"));
        taskMaster.setLocation(document.getString("location"));
        taskMaster.setRating(document.getDouble("rating") != null ? document.getDouble("rating") : 0.0);
        taskMaster.setDescription(document.getString("description"));
        taskMaster.setHourlyRateUsd(document.getDouble("hourlyRateUsd") != null ? document.getDouble("hourlyRateUsd") : 0.0);

        ArrayList<String> jobCategories = (ArrayList<String>) document.get("jobCategories");
        Optional.ofNullable(jobCategories)
                .ifPresent(value -> taskMaster.setJobCategories(value.toArray(new String[0])));

        return taskMaster;
    }
}
