package com.bookstore.productsevice;

import com.bookstore.productsevice.model.TaskMaster;
import com.bookstore.productsevice.repository.TaskMasterRepository;
import com.bookstore.productsevice.security.Secret;
import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.CommandLineRunner;
import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.boot.web.servlet.ServletComponentScan;
import org.springframework.cloud.client.ServiceInstance;
import org.springframework.cloud.client.discovery.DiscoveryClient;
import org.springframework.cloud.client.discovery.EnableDiscoveryClient;
import org.springframework.cloud.client.loadbalancer.LoadBalanced;
import org.springframework.context.annotation.Bean;
import org.springframework.core.io.ClassPathResource;
import org.springframework.core.ParameterizedTypeReference;
import org.springframework.core.env.Environment;
import org.springframework.http.HttpMethod;
import org.springframework.http.ResponseEntity;
import org.springframework.retry.annotation.Backoff;
import org.springframework.retry.annotation.EnableRetry;
import org.springframework.retry.annotation.Retryable;
import org.springframework.web.client.RestClientException;
import org.springframework.web.client.RestTemplate;

import java.net.ConnectException;
import java.net.URI;
import java.io.InputStream;
import java.util.List;


@SpringBootApplication
@EnableDiscoveryClient
@EnableRetry
@ServletComponentScan
public class TaskServiceApplication {

    Logger logger = LoggerFactory.getLogger(TaskServiceApplication.class);

    @Autowired
    private TaskMasterRepository repository;

    @Autowired
    Secret secret;


    RestTemplate restTemplate = new RestTemplate();

    @Autowired
    DiscoveryClient discoveryClient;

    @Autowired
    Environment environment;

    public static void main(String[] args) {
        SpringApplication.run(TaskServiceApplication.class, args);
    }


    @Bean

    public CommandLineRunner setSecretKey() {

        return new CommandLineRunner() {
            @Override
            @Retryable(backoff = @Backoff(delay = 5000))
            public void run(String... args) throws Exception {
                logger.info("Consul Demo - Getting Secret Key");

                List<ServiceInstance> instances = discoveryClient.getInstances("authentication-service");

                for(String profile : environment.getActiveProfiles()) {
                    if(profile.equals("test")) {
                        return;
                    }
                }

                if (instances != null && instances.size() > 0 ) {
                    URI uri = new URI(instances.get(0).getUri().toString() +  "/getSecretKey");
                    ResponseEntity<Secret> response  = restTemplate.getForEntity(uri,Secret.class);
                    logger.info("Response Received as " + response + " -  ");
                    secret.setKey(response.getBody().key);
                } else {
                    throw new ConnectException();
                }

                return;
            }
        };
    }

    @Bean
    public CommandLineRunner createDatabase(){
        return new CommandLineRunner() {
            @Override
            public void run(String... args) throws Exception {
                if (repository.count() > 0) {
                    logger.info("Task masters collection already has data; skipping seed.");
                    return;
                }

                ObjectMapper objectMapper = new ObjectMapper();
                objectMapper.configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false);

                ClassPathResource seedResource = new ClassPathResource("seed/taskMasters.json");
                try (InputStream inputStream = seedResource.getInputStream()) {
                    SeedTaskMastersPayload payload = objectMapper.readValue(inputStream, SeedTaskMastersPayload.class);

                    if (payload == null || payload.taskMasters == null || payload.taskMasters.isEmpty()) {
                        logger.warn("No task masters found in seed/taskMasters.json; product catalog remains empty.");
                        return;
                    }

                    repository.saveAll(payload.taskMasters);
                    logger.info("Seeded {} task masters from seed/taskMasters.json", payload.taskMasters.size());
                }
            }
        };

    };

    private static class SeedTaskMastersPayload {
        public List<TaskMaster> taskMasters;
    }

}

