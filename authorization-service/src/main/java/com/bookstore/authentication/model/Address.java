package com.bookstore.authentication.model;

import com.fasterxml.jackson.annotation.JsonProperty;
import lombok.Getter;
import lombok.Setter;

@Getter
@Setter
public class Address {

    @JsonProperty
    private String addressLine;

    @JsonProperty
    private String postalCode;

    @JsonProperty
    private Country country;

    @JsonProperty
    private String city;


}
