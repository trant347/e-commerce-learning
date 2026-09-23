package com.bookstore.productsevice.seed;

import com.bookstore.productsevice.category.CategoryService;
import com.bookstore.productsevice.location.LocationNormalizer;
import com.bookstore.productsevice.location.NormalizedLocation;
import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.ApplicationRepository;
import com.bookstore.productsevice.repository.CategoryRepository;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.core.io.ClassPathResource;
import org.springframework.core.io.Resource;
import org.springframework.stereotype.Component;

import java.io.IOException;
import java.io.InputStream;
import java.util.Arrays;
import java.util.List;

@Component
public class ProductDataSeeder {

    private static final Logger log = LoggerFactory.getLogger(ProductDataSeeder.class);
    private static final String SEED_ACTOR = "system-seed";

    private final CategoryRepository categoryRepository;
    private final TaskMasterRepository taskMasterRepository;
    private final ApplicationRepository applicationRepository;
    private final CategoryService categoryService;
    private final LocationNormalizer locationNormalizer;
    private final ObjectMapper objectMapper;
    private final Resource categorySeedResource;
    private final Resource taskMasterSeedResource;

    @Autowired
    public ProductDataSeeder(CategoryRepository categoryRepository,
                             TaskMasterRepository taskMasterRepository,
                             ApplicationRepository applicationRepository,
                             CategoryService categoryService,
                             LocationNormalizer locationNormalizer,
                             ObjectMapper objectMapper) {
        this(
                categoryRepository,
                taskMasterRepository,
                applicationRepository,
                categoryService,
                locationNormalizer,
                objectMapper,
                new ClassPathResource("seed/categories.json"),
                new ClassPathResource("seed/taskMasters.json"));
    }

    ProductDataSeeder(CategoryRepository categoryRepository,
                      TaskMasterRepository taskMasterRepository,
                      ApplicationRepository applicationRepository,
                      CategoryService categoryService,
                      LocationNormalizer locationNormalizer,
                      ObjectMapper objectMapper,
                      Resource categorySeedResource,
                      Resource taskMasterSeedResource) {
        this.categoryRepository = categoryRepository;
        this.taskMasterRepository = taskMasterRepository;
        this.applicationRepository = applicationRepository;
        this.categoryService = categoryService;
        this.locationNormalizer = locationNormalizer;
        this.objectMapper = objectMapper;
        this.categorySeedResource = categorySeedResource;
        this.taskMasterSeedResource = taskMasterSeedResource;
    }

    public void seedIfEmpty() throws IOException {
        long categoryCount = categoryRepository.count();
        long taskMasterCount = taskMasterRepository.count();
        long applicationCount = applicationRepository.count();

        if (categoryCount == 0 && (taskMasterCount > 0 || applicationCount > 0)) {
            throw new IllegalStateException(
                    "Legacy product data exists without the category catalog. "
                            + "Reset the products database before starting product-service.");
        }

        seedMissingCategories(readCategorySeeds());

        if (taskMasterCount > 0) {
            log.info("Task masters collection already has data; skipping TaskMaster seed.");
            return;
        }

        List<TaskMaster> taskMasters = readTaskMasterSeeds();
        if (taskMasters.isEmpty()) {
            log.warn("No task masters found in seed/taskMasters.json; product catalog remains empty.");
            return;
        }

        for (TaskMaster taskMaster : taskMasters) {
            NormalizedLocation location = locationNormalizer.normalizeForWrite(taskMaster.getLocation());
            List<String> categoryIds = categoryService.normalizeAndValidateCategoryIds(
                    Arrays.asList(taskMaster.getJobCategories()));
            taskMaster.setLocation(location.displayLocation())
                    .setLocationCity(location.city())
                    .setLocationStateCode(location.stateCode())
                    .setJobCategories(categoryIds.toArray(String[]::new));
        }

        taskMasterRepository.saveAll(taskMasters);
        log.info("Seeded {} task masters from seed/taskMasters.json", taskMasters.size());
    }

    private void seedMissingCategories(List<CategorySeed> categories) {
        if (categories.isEmpty()) {
            throw new IllegalStateException("No categories found in seed/categories.json.");
        }

        int createdCount = 0;
        for (CategorySeed category : categories) {
            String categoryId = categoryService.normalizeId(category.id);
            if (categoryRepository.existsById(categoryId)) {
                continue;
            }
            categoryService.createCategory(
                    categoryId,
                    category.displayName,
                    category.description,
                    SEED_ACTOR);
            createdCount++;
        }

        if (createdCount > 0) {
            log.info("Seeded {} categories from seed/categories.json", createdCount);
        } else {
            log.info("Category catalog already contains all seed categories.");
        }
    }

    private List<CategorySeed> readCategorySeeds() throws IOException {
        try (InputStream inputStream = categorySeedResource.getInputStream()) {
            CategorySeedPayload payload = objectMapper.readValue(inputStream, CategorySeedPayload.class);
            return payload == null || payload.categories == null ? List.of() : payload.categories;
        }
    }

    private List<TaskMaster> readTaskMasterSeeds() throws IOException {
        try (InputStream inputStream = taskMasterSeedResource.getInputStream()) {
            TaskMasterSeedPayload payload = objectMapper.readValue(inputStream, TaskMasterSeedPayload.class);
            return payload == null || payload.taskMasters == null ? List.of() : payload.taskMasters;
        }
    }

    static class CategorySeedPayload {
        public List<CategorySeed> categories;
    }

    static class CategorySeed {
        public String id;
        public String displayName;
        public String description;
    }

    static class TaskMasterSeedPayload {
        public List<TaskMaster> taskMasters;
    }
}
