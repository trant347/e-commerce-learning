package com.bookstore.authentication.controllers;

import com.bookstore.authentication.configs.JwtConfig;
import com.bookstore.authentication.configs.Secret;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
public class SecretKeyController {

    @Autowired
    JwtConfig jwtConfig;

    @GetMapping(value = "/getSecretKey")
    public ResponseEntity<Secret> getSecret() {
        return new ResponseEntity<>(new Secret(jwtConfig.getSecret(), null), HttpStatus.OK);
    }


}
