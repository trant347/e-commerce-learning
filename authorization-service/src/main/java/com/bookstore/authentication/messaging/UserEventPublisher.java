package com.bookstore.authentication.messaging;

import com.fasterxml.jackson.core.JsonProcessingException;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.kafka.core.KafkaTemplate;
import org.springframework.stereotype.Component;

import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Map;

@Component
public class UserEventPublisher {

    public static final String TOPIC = "user-events";
    public static final String USER_DELETED = "USER_DELETED";

    private static final Logger logger = LoggerFactory.getLogger(UserEventPublisher.class);

    private final KafkaTemplate<String, String> kafkaTemplate;
    private final ObjectMapper objectMapper = new ObjectMapper();

    @Autowired
    public UserEventPublisher(KafkaTemplate<String, String> kafkaTemplate) {
        this.kafkaTemplate = kafkaTemplate;
    }

    /**
     * Publish synchronously: blocks until the broker ack so the caller can
     * abort the database delete if the broker is unreachable.
     */
    public void publishUserDeleted(String username, String deletedByUsername) {
        Map<String, Object> payload = new LinkedHashMap<>();
        payload.put("type", USER_DELETED);
        payload.put("username", username);
        payload.put("deletedAt", Instant.now().toString());
        payload.put("deletedByUsername", deletedByUsername);

        String json;
        try {
            json = objectMapper.writeValueAsString(payload);
        } catch (JsonProcessingException e) {
            throw new IllegalStateException("Failed to serialise user-event", e);
        }

        try {
            kafkaTemplate.send(TOPIC, username, json).get();
            logger.info("Published USER_DELETED for username='{}'", username);
        } catch (Exception e) {
            throw new RuntimeException("Failed to publish USER_DELETED event for " + username, e);
        }
    }
}
