package com.bookstore.productsevice.messaging;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.kafka.core.KafkaTemplate;
import org.springframework.stereotype.Component;

@Component
public class CategoryEventPublisher {

    private static final Logger log = LoggerFactory.getLogger(CategoryEventPublisher.class);
    private static final String TOPIC = "categories-updated";

    private final KafkaTemplate<String, String> kafkaTemplate;

    public CategoryEventPublisher(KafkaTemplate<String, String> kafkaTemplate) {
        this.kafkaTemplate = kafkaTemplate;
    }

    public void publishCategoriesUpdated() {
        log.info("[CategoryEventPublisher] Publishing event to topic={}", TOPIC);
        kafkaTemplate.send(TOPIC, "categories-updated");
    }
}
