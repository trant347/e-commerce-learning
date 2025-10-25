package com.bookstore.productsevice.security;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.cloud.client.loadbalancer.LoadBalanced;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.web.client.RestTemplate;

import java.time.LocalTime;


@Configuration
public class Secret {

    @Value("JwtSecretKey")
    public String key;

    public LocalTime expirationTime;

    public LocalTime getExpirationTime() {
        return expirationTime;
    }

    public String getKey() {
        return key;
    }

    public void setKey(String secret) {
        this.key = secret;
    }

    public void setExpirationTime(LocalTime expirationTime) {
        this.expirationTime = expirationTime;
    }
}
