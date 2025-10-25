package com.bookstore.authentication.configs;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.context.annotation.Configuration;

import java.time.LocalTime;


public class Secret {

    public String key;
    public LocalTime expirationTime;

    public Secret(String key, LocalTime expirationTime) {
        this.key = key;
        this.expirationTime = expirationTime;
    }

    public LocalTime getExpirationTime() {
        return expirationTime;
    }

    public String getKey() {
        return key;
    }

    public void setKey(String key) {
        this.key = key;
    }

    public void setExpirationTime(LocalTime time) {
        this.expirationTime = time;
    }

}
