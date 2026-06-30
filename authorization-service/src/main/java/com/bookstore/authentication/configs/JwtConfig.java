package com.bookstore.authentication.configs;

import jakarta.annotation.PostConstruct;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.context.annotation.Configuration;
import org.springframework.context.annotation.PropertySource;

import java.nio.charset.StandardCharsets;


@Configuration
@PropertySource("classpath:security.properties")
public class JwtConfig {
    @Value("${security.jwt.uri:/login/**}")
    private String Uri;

    @Value("${security.jwt.header:Authorization}")
    private String header;

    @Value("${security.jwt.prefix:Bearer }")
    private String prefix;

    @Value("${security.jwt.expiration:#{24*60*60}}")
    private int expiration;

    @Value("${security.jwt.secret:JwtSecretKey}")
    private String secret;

    public String getUri() {
        return Uri;
    }

    public void setUri(String uri) {
        Uri = uri;
    }

    public String getHeader() {
        return header;
    }

    public void setHeader(String header) {
        this.header = header;
    }

    public String getPrefix() {
        return prefix;
    }

    public void setPrefix(String prefix) {
        this.prefix = prefix;
    }

    public int getExpiration() {
        return expiration;
    }

    public void setExpiration(int expiration) {
        this.expiration = expiration;
    }

    public String getSecret() {
        return secret;
    }

    public void setSecret(String secret) {
        this.secret = secret;
    }

    @PostConstruct
    public void validateSecretStrength() {
        if (secret == null || secret.isBlank()) {
            throw new IllegalStateException("security.jwt.secret is missing. Set SECURITY_JWT_SECRET/JwtSecret to a strong value (>= 32 chars).");
        }

        int secretBytes = secret.getBytes(StandardCharsets.UTF_8).length;
        if (secretBytes < 32) {
            throw new IllegalStateException("security.jwt.secret is too short (" + secretBytes + " bytes). Use at least 32 bytes for HS256.");
        }
    }
}