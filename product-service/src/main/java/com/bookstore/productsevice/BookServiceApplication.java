package com.bookstore.productsevice;

import com.bookstore.productsevice.model.Book;
import com.bookstore.productsevice.repository.BookRepository;
import com.bookstore.productsevice.security.Secret;
import com.thedeanda.lorem.Lorem;
import com.thedeanda.lorem.LoremIpsum;
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
import java.util.List;


@SpringBootApplication
@EnableDiscoveryClient
@EnableRetry
@ServletComponentScan
public class BookServiceApplication {

    Logger logger = LoggerFactory.getLogger(BookServiceApplication.class);

    @Autowired
    private BookRepository repository;

    @Autowired
    Secret secret;


    RestTemplate restTemplate = new RestTemplate();

    @Autowired
    DiscoveryClient discoveryClient;

    @Autowired
    Environment environment;

    public static void main(String[] args) {
        SpringApplication.run(BookServiceApplication.class, args);
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

                Lorem lorem = LoremIpsum.getInstance();

                String[] categories = new String[] { "fiction", "non-fiction", "autobiography", "detective", "science"};

                repository.deleteAll();

                for(int i = 1; i <= 18; i++) {

                    Book book = new Book.Builder()
                                    .setName( lorem.getWords(4,6))
                                    .setAuthors(new String[] { lorem.getName(), lorem.getName()})
                                    .setDescription(lorem.getWords(10,20))
                                    .setCategories(new String[] { categories[(int)Math.random()*5]})
                                    .setRating(Math.random()*10)
                                    .setPriceUsd((double)Math.round(100*Math.random())/100)
                                    .setPicture("https://picsum.photos/200/300?id="+(int)(100*Math.random()))
                                    .build();
                    repository.save(book);
                }
            }
        };

    };

}

