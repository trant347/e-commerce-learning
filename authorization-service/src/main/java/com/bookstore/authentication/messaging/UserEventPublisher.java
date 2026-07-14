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
    public static final String USER_REGISTERED = "USER_REGISTERED";

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

    /**
     * Publish synchronously: blocks until the broker ack. Unlike publishUserDeleted, callers
     * (see UserController.createUser) treat a failure here as best-effort — a lost/undelivered
     * USER_REGISTERED event only means payment-service's wallet-creation consumer misses this
     * user, and payment-service's own lazy wallet-creation safety net (see
     * WalletSimulationPaymentGateway) covers that gap on the user's first charge, so it should
     * not block registration itself.
     */
    public void publishUserRegistered(String username) {
        Map<String, Object> payload = new LinkedHashMap<>();
        payload.put("type", USER_REGISTERED);
        payload.put("username", username);
        payload.put("registeredAt", Instant.now().toString());

        String json;
        try {
            json = objectMapper.writeValueAsString(payload);
        } catch (JsonProcessingException e) {
            throw new IllegalStateException("Failed to serialise user-event", e);
        }

        try {
            kafkaTemplate.send(TOPIC, username, json).get();
            logger.info("Published USER_REGISTERED for username='{}'", username);
        } catch (Exception e) {
            throw new RuntimeException("Failed to publish USER_REGISTERED event for " + username, e);
        }
    }
}
