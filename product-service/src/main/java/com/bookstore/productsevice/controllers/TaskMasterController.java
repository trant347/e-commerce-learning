package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.exception.ItemNotFoundException;
import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.bookstore.productsevice.services.queries.TaskMasterSearchService;
import com.bookstore.productsevice.storage.StorageService;
import com.bookstore.productsevice.validators.TaskMasterValidator;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;

import java.util.HashMap;
import java.util.List;
import java.util.Map;

@RestController
@RequestMapping("/products")
public class TaskMasterController {

    private static final int ITEM_PER_PAGE = 20;

    @Autowired
    public TaskMasterRepository taskMasterRepository;

    @Autowired
    public StorageService storageService;

    @Autowired
    private TaskMasterSearchService taskMasterSearchService;

    @GetMapping(params = "name")
    public ResponseEntity<List<TaskMaster>> getTaskMastersByName(@RequestParam String name) {
        List<TaskMaster> taskMasters = taskMasterRepository.findAllByName(name);
        if (taskMasters.isEmpty()) {
            throw new ItemNotFoundException(name);
        }
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }

    @GetMapping(params = "location")
    public ResponseEntity<List<TaskMaster>> getTaskMastersByLocation(@RequestParam String location) {
        List<TaskMaster> taskMasters = taskMasterRepository.findAllByLocation(location);
        if (taskMasters.isEmpty()) {
            throw new ItemNotFoundException("location: " + location);
        }
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }

    @GetMapping(params = "category")
    public ResponseEntity<List<TaskMaster>> getTaskMastersByCategory(@RequestParam String category) {
        List<TaskMaster> taskMasters = taskMasterRepository.findAllByJobCategoriesContaining(category);
        if (taskMasters.isEmpty()) {
            throw new ItemNotFoundException("category: " + category);
        }
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }

    @GetMapping
    public ResponseEntity<List<TaskMaster>> getTaskMasters(
            @RequestParam(required = false) Integer page,
            @RequestParam(required = false) Integer limit) {
        List<TaskMaster> taskMasters = taskMasterRepository.findAll();
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }

    @PostMapping("/tests")
    public ResponseEntity<Void> saveTaskMastersTest(@RequestBody List<TaskMaster> taskMasters) {
        return new ResponseEntity<>(HttpStatus.OK);
    }

    @PostMapping
    public ResponseEntity<TaskMaster> createTaskMaster(@RequestBody TaskMaster taskMaster) throws Exception {
        TaskMasterValidator.validate(taskMaster);
        TaskMaster response = taskMasterRepository.save(taskMaster);
        return new ResponseEntity<>(response, HttpStatus.OK);
    }

    @GetMapping("/{id}")
    public ResponseEntity<TaskMaster> getTaskMasterById(@PathVariable String id) {
        TaskMaster taskMaster = taskMasterRepository.findById(id)
                .orElseThrow(() -> new ItemNotFoundException(id));
        return new ResponseEntity<>(taskMaster, HttpStatus.OK);
    }

    @GetMapping("/facet-search")
    public ResponseEntity<?> getTaskMastersWithFacet(
            @RequestParam String name,
            @RequestParam(required = false) Integer page,
            @RequestParam(required = false) String[] sortedFields) {

        if (page == null) {
            page = 0;
        }
        if (sortedFields == null) {
            sortedFields = new String[]{"rating", "hourlyRateUsd"};
        }

        Map<String, ?> results = taskMasterSearchService.getTaskMastersByNameFacetSearch(name, page, ITEM_PER_PAGE, sortedFields);

        if (results.get("taskMasters") == null) {
            return ResponseEntity.notFound().build();
        }

        Map<String, Object> facets = new HashMap<>();
        facets.put("hourlyRateUsd", results.get("hourlyRateUsd"));
        facets.put("rating", results.get("rating"));

        HashMap<String, Object> response = new HashMap<>();
        response.put("taskMasters", results.get("taskMasters"));
        response.put("facets", facets);
        response.put("page", page);
        return ResponseEntity.ok(response);
    }

    @GetMapping("/by-rating")
    public ResponseEntity<List<TaskMaster>> getTaskMastersByMinRating(@RequestParam double minRating) {
        List<TaskMaster> taskMasters = taskMasterRepository.findTaskMasterByRatingGreaterThanEqual(minRating);
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }

    @GetMapping("/by-rate-range")
    public ResponseEntity<List<TaskMaster>> getTaskMastersByRateRange(
            @RequestParam double minRate,
            @RequestParam double maxRate) {
        List<TaskMaster> taskMasters = taskMasterRepository.findTaskMasterByHourlyRateUsdBetween(minRate, maxRate);
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }
}
