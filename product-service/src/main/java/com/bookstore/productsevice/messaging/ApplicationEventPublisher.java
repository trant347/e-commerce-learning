package com.bookstore.productsevice.messaging;

import com.fasterxml.jackson.core.JsonProcessingException;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.kafka.core.KafkaTemplate;
import org.springframework.stereotype.Component;

import java.util.Map;

/**
 * Publishes task-master application lifecycle events to the notification-events Kafka topic.
 * The notification-service consumes this topic and delivers notifications to the target user.
 */
@Component
public class ApplicationEventPublisher {

    private static final Logger log = LoggerFactory.getLogger(ApplicationEventPublisher.class);
    private static final String TOPIC = "notification-events";

    private final KafkaTemplate<String, String> kafkaTemplate;
    private final ObjectMapper objectMapper;

    public ApplicationEventPublisher(KafkaTemplate<String, String> kafkaTemplate,
                                     ObjectMapper objectMapper) {
        this.kafkaTemplate = kafkaTemplate;
        this.objectMapper = objectMapper;
    }

    /**
     * Notifies the admin that a new application is waiting for review.
     *
     * @param applicationId  the ID of the submitted application
     * @param applicantName  human-readable name of the applicant (for the notification message)
     */
    public void publishApplicationSubmitted(String applicationId, String applicantName) {
        publish(Map.of(
                "type",                "TASKMASTER_APPLICATION_SUBMITTED",
                "recipientUsername",   "admin",
                "message",             applicantName + " has applied to become a TaskMaster.",
                "actionUrl",           "/admin/applications/" + applicationId
        ));
    }

    /**
     * Notifies the applicant that their application was accepted.
     *
     * @param applicantUsername the username to notify
     * @param taskMasterId      the newly created task master profile ID
     */
    public void publishApplicationAccepted(String applicantUsername, String taskMasterId) {
        publish(Map.of(
                "type",                "TASKMASTER_APPLICATION_ACCEPTED",
                "recipientUsername",   applicantUsername,
                "message",             "Congratulations! Your TaskMaster application has been accepted.",
                "actionUrl",           "/product/" + taskMasterId
        ));
    }

    /**
     * Notifies the applicant that their application was declined.
     *
     * @param applicantUsername the username to notify
     * @param reason            optional reason provided by the admin
     */
    public void publishApplicationDeclined(String applicantUsername, String reason) {
        String message = reason != null && !reason.isBlank()
                ? "Your TaskMaster application was declined: " + reason
                : "Your TaskMaster application was declined.";
        publish(Map.of(
                "type",              "TASKMASTER_APPLICATION_DECLINED",
                "recipientUsername", applicantUsername,
                "message",           message,
                "actionUrl",         "/"
        ));
    }

    private void publish(Map<String, String> payload) {
        try {
            String json = objectMapper.writeValueAsString(payload);
            log.info("[ApplicationEventPublisher] Publishing to topic={} payload={}", TOPIC, json);
            kafkaTemplate.send(TOPIC, json);
        } catch (JsonProcessingException e) {
            log.error("[ApplicationEventPublisher] Failed to serialise event payload", e);
        }
    }
}
