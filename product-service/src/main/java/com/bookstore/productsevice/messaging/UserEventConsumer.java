package com.bookstore.productsevice.messaging;

import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.ApplicationRepository;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.bookstore.productsevice.services.ProductCacheService;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.kafka.annotation.KafkaListener;
import org.springframework.stereotype.Component;

@Component
public class UserEventConsumer {

    public static final String TOPIC = "user-events";
    private static final String USER_DELETED = "USER_DELETED";

    private static final Logger logger = LoggerFactory.getLogger(UserEventConsumer.class);

    private final TaskMasterRepository taskMasterRepository;
    private final ApplicationRepository applicationRepository;
    private final ProductCacheService productCacheService;
    private final ObjectMapper objectMapper = new ObjectMapper();

    @Autowired
    public UserEventConsumer(TaskMasterRepository taskMasterRepository,
                             ApplicationRepository applicationRepository,
                             ProductCacheService productCacheService) {
        this.taskMasterRepository = taskMasterRepository;
        this.applicationRepository = applicationRepository;
        this.productCacheService = productCacheService;
    }

    @KafkaListener(topics = TOPIC, groupId = "product-service-user-events")
    public void onUserEvent(String message) {
        JsonNode node;
        try {
            node = objectMapper.readTree(message);
        } catch (Exception e) {
            // Bad payload: log and swallow so we don't block the partition forever.
            logger.error("Unparseable user-event payload: {}", message, e);
            return;
        }

        String type = node.path("type").asText("");
        String username = node.path("username").asText(null);

        if (!USER_DELETED.equals(type)) {
            logger.debug("Ignoring user-event of type '{}'", type);
            return;
        }
        if (username == null || username.isEmpty()) {
            logger.warn("USER_DELETED event missing 'username' field: {}", message);
            return;
        }

        // Look up the task master first so we can evict its cache entry.
        TaskMaster taskMaster = taskMasterRepository.findByOwnerUsername(username).orElse(null);

        // Idempotent: deleting zero documents is a normal outcome on redelivery.
        long taskMastersDeleted = taskMasterRepository.deleteByOwnerUsername(username);
        long applicationsDeleted = applicationRepository.deleteAllByApplicantUsername(username);

        if (taskMaster != null) {
            productCacheService.evictOnDelete(taskMaster.getId());
        }

        logger.info("USER_DELETED cascade for '{}': taskMasters={}, applications={}",
                username, taskMastersDeleted, applicationsDeleted);
    }
}
