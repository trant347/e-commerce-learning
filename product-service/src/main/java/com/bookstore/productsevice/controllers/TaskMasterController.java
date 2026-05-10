package com.bookstore.productsevice.controllers;

import com.bookstore.productsevice.exception.ItemNotFoundException;
import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.bookstore.productsevice.services.queries.TaskMasterSearchService;
import com.bookstore.productsevice.storage.StorageService;
import com.bookstore.productsevice.validators.TaskMasterValidator;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
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

    private static final Logger log = LoggerFactory.getLogger(TaskMasterController.class);

    private static final int ITEM_PER_PAGE = 20;

    @Autowired
    public TaskMasterRepository taskMasterRepository;

    @Autowired
    public StorageService storageService;

    @Autowired
    private TaskMasterSearchService taskMasterSearchService;

    @GetMapping(params = "name")
    public ResponseEntity<List<TaskMaster>> getTaskMastersByName(@RequestParam String name) {
        log.debug("[TaskMasterController] GET /products?name='{}'", name);
        List<TaskMaster> taskMasters = taskMasterRepository.findAllByName(name);
        log.debug("[TaskMasterController] name='{}' → {} results", name, taskMasters.size());
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }

    @GetMapping(params = "location")
    public ResponseEntity<List<TaskMaster>> getTaskMastersByLocation(@RequestParam String location) {
        log.debug("[TaskMasterController] GET /products?location='{}'", location);
        List<TaskMaster> taskMasters = taskMasterRepository.findAllByLocation(location);
        log.debug("[TaskMasterController] location='{}' → {} results", location, taskMasters.size());
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }

    @GetMapping(params = "category")
    public ResponseEntity<List<TaskMaster>> getTaskMastersByCategory(@RequestParam String category) {
        log.debug("[TaskMasterController] GET /products?category='{}'", category);
        List<TaskMaster> taskMasters = taskMasterRepository.findAllByJobCategoriesContaining(category);
        log.debug("[TaskMasterController] category='{}' → {} results", category, taskMasters.size());
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }

    @GetMapping
    public ResponseEntity<List<TaskMaster>> getTaskMasters(
            @RequestParam(required = false) Integer page,
            @RequestParam(required = false) Integer limit) {
        log.debug("[TaskMasterController] GET /products page={} limit={}", page, limit);
        List<TaskMaster> taskMasters = taskMasterRepository.findAll();
        log.debug("[TaskMasterController] getAll → {} results", taskMasters.size());
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }

    @PostMapping("/tests")
    public ResponseEntity<Void> saveTaskMastersTest(@RequestBody List<TaskMaster> taskMasters) {
        log.debug("[TaskMasterController] POST /products/tests payload size={}", taskMasters.size());
        return new ResponseEntity<>(HttpStatus.OK);
    }

    @PostMapping
    public ResponseEntity<TaskMaster> createTaskMaster(@RequestBody TaskMaster taskMaster) throws Exception {
        log.debug("[TaskMasterController] POST /products name='{}'", taskMaster.getName());
        TaskMasterValidator.validate(taskMaster);
        TaskMaster response = taskMasterRepository.save(taskMaster);
        log.debug("[TaskMasterController] Created task master id='{}'", response.getId());
        return new ResponseEntity<>(response, HttpStatus.OK);
    }

    @GetMapping("/{id}")
    public ResponseEntity<TaskMaster> getTaskMasterById(@PathVariable String id) {
        log.debug("[TaskMasterController] GET /products/{}", id);
        TaskMaster taskMaster = taskMasterRepository.findById(id)
                .orElseThrow(() -> new ItemNotFoundException(id));
        log.debug("[TaskMasterController] Found task master id='{}' name='{}'", id, taskMaster.getName());
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
            log.warn("[TaskMasterController] facet-search name='{}' page={} → no taskMasters in result", name, page);
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
        log.debug("[TaskMasterController] GET /products/by-rating minRating={}", minRating);
        List<TaskMaster> taskMasters = taskMasterRepository.findTaskMasterByRatingGreaterThanEqual(minRating);
        log.debug("[TaskMasterController] by-rating minRating={} → {} results", minRating, taskMasters.size());
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }

    @GetMapping("/by-rate-range")
    public ResponseEntity<List<TaskMaster>> getTaskMastersByRateRange(
            @RequestParam double minRate,
            @RequestParam double maxRate) {
        log.debug("[TaskMasterController] GET /products/by-rate-range minRate={} maxRate={}", minRate, maxRate);
        List<TaskMaster> taskMasters = taskMasterRepository.findTaskMasterByHourlyRateUsdBetween(minRate, maxRate);
        log.debug("[TaskMasterController] by-rate-range [{}, {}] → {} results", minRate, maxRate, taskMasters.size());
        return new ResponseEntity<>(taskMasters, HttpStatus.OK);
    }
}
