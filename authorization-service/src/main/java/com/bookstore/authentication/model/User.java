package com.bookstore.authentication.model;

import com.fasterxml.jackson.annotation.JsonIgnore;
import com.fasterxml.jackson.annotation.JsonProperty;
import lombok.Builder;
import lombok.Getter;
import lombok.Setter;
import lombok.experimental.Accessors;
import org.springframework.data.annotation.Id;

import java.util.List;


@Getter
@Setter
@Accessors(chain = true)
public class User {

    @Id
    private String userId;
    private String username;
    private String firstName;
    private String lastName;
    private Gender gender;
    private String email;
    private String phoneNumber;

    @JsonProperty(value = "address")
    private Address address;

    private String role;

    @JsonProperty(value = "password")
    private String password;
    @JsonIgnore
    private String encrytedPassword;

    public User() {

    }

}
