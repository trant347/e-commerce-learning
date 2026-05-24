package com.bookstore.productsevice.messaging;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.Before;
import org.junit.Test;
import org.mockito.ArgumentCaptor;
import org.springframework.kafka.core.KafkaTemplate;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.verify;

/**
 * Locks in the notification payload contract between product-service and notification-service.
 * The frontend resolves actionType + actionPayload to a route, so these field names and
 * values must stay stable. Older revisions used "actionUrl" — that coupling has been removed.
 */
public class ApplicationEventPublisherTest {

    private KafkaTemplate<String, String> kafkaTemplate;
    private ObjectMapper objectMapper;
    private ApplicationEventPublisher publisher;

    @Before
    @SuppressWarnings("unchecked")
    public void setUp() {
        kafkaTemplate = mock(KafkaTemplate.class);
        objectMapper = new ObjectMapper();
        publisher = new ApplicationEventPublisher(kafkaTemplate, objectMapper);
    }

    @Test
    public void publishApplicationSubmitted_sendsAdminNotificationWithApplicationId() throws Exception {
        publisher.publishApplicationSubmitted("app-123", "steventran");

        JsonNode payload = captureSentPayload();

        assertThat(payload.get("type").asText()).isEqualTo("TASKMASTER_APPLICATION_SUBMITTED");
        assertThat(payload.get("recipientUsername").asText()).isEqualTo("admin");
        assertThat(payload.get("message").asText()).contains("steventran");
        assertThat(payload.get("actionType").asText()).isEqualTo("VIEW_ADMIN_APPLICATION");
        assertThat(payload.get("actionPayload").get("applicationId").asText()).isEqualTo("app-123");

        // Old contract must be gone — pin the decoupling
        assertThat(payload.has("actionUrl")).isFalse();
    }

    @Test
    public void publishApplicationAccepted_sendsApplicantNotificationPointingToMyApplication() throws Exception {
        publisher.publishApplicationAccepted("alice", "tm-456");

        JsonNode payload = captureSentPayload();

        assertThat(payload.get("type").asText()).isEqualTo("TASKMASTER_APPLICATION_ACCEPTED");
        assertThat(payload.get("recipientUsername").asText()).isEqualTo("alice");
        assertThat(payload.get("actionType").asText()).isEqualTo("VIEW_MY_APPLICATION");
        assertThat(payload.has("actionUrl")).isFalse();
    }

    @Test
    public void publishApplicationDeclined_withReason_includesReasonInMessage() throws Exception {
        publisher.publishApplicationDeclined("bob", "Insufficient experience");

        JsonNode payload = captureSentPayload();

        assertThat(payload.get("type").asText()).isEqualTo("TASKMASTER_APPLICATION_DECLINED");
        assertThat(payload.get("recipientUsername").asText()).isEqualTo("bob");
        assertThat(payload.get("message").asText()).contains("Insufficient experience");
        assertThat(payload.get("actionType").asText()).isEqualTo("VIEW_MY_APPLICATION");
    }

    @Test
    public void publishApplicationDeclined_withBlankReason_omitsReasonFromMessage() throws Exception {
        publisher.publishApplicationDeclined("bob", "  ");

        JsonNode payload = captureSentPayload();

        assertThat(payload.get("message").asText()).doesNotContain(":");
        assertThat(payload.get("message").asText()).isEqualTo("Your TaskMaster application was declined.");
    }

    private JsonNode captureSentPayload() throws Exception {
        ArgumentCaptor<String> captor = ArgumentCaptor.forClass(String.class);
        verify(kafkaTemplate).send(eq("notification-events"), captor.capture());
        return objectMapper.readTree(captor.getValue());
    }
}
