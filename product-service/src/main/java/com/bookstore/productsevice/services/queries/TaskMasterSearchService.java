package com.bookstore.productsevice.services.queries;

import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.TaskMasterMapper;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import org.bson.Document;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.stereotype.Service;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.Map;

import static com.bookstore.productsevice.repository.FacetRepository.HOURLY_RATE_BUCKETS_NAME;
import static com.bookstore.productsevice.repository.FacetRepository.HOURLY_RATE_USD_RANGES;
import static com.bookstore.productsevice.repository.FacetRepository.RATING_BUCKETS_NAME;
import static com.bookstore.productsevice.repository.FacetRepository.RATING_RANGES;

@Service
public class TaskMasterSearchService implements SearchService {

    private TaskMasterRepository taskMasterRepository;

    @Autowired
    public TaskMasterSearchService(TaskMasterRepository taskMasterRepository) {
        this.taskMasterRepository = taskMasterRepository;
    }

    public Map<String, ?> getTaskMastersByNameFacetSearch(String name, int page, int itemsPerPage, String[] sortFields) {
        Map<String, Object> results = new HashMap<>();
        Document document = taskMasterRepository.getTaskMastersUsingNameFacetSearch(name, page, itemsPerPage, sortFields).orElse(null);

        if (document == null) {
            return results;
        }

        ArrayList<TaskMaster> taskMasters = new ArrayList<>();
        ArrayList<Document> taskMasterList = (ArrayList<Document>) document.get("taskMasters");

        if (taskMasterList != null) {
            taskMasterList.forEach(tm -> taskMasters.add(TaskMasterMapper.mapBsonToTaskMaster(tm)));
        }

        results.put("taskMasters", taskMasters);

        Document rating = new Document();
        rating.put("items", document.get(RATING_BUCKETS_NAME));
        rating.put("ranges", RATING_RANGES);
        results.put("rating", rating);

        Document hourlyRateUsd = new Document();
        hourlyRateUsd.put("items", document.get(HOURLY_RATE_BUCKETS_NAME));
        hourlyRateUsd.put("ranges", HOURLY_RATE_USD_RANGES);
        results.put("hourlyRateUsd", hourlyRateUsd);

        return results;
    }
}
