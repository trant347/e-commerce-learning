package com.bookstore.authentication.model;


import lombok.Getter;
import lombok.Setter;

@Getter
@Setter
public class Country extends Lookup {

    private String region;

    public Country(String code, String label, String region) {
        super(code, label, "country");
        this.region = region;
    }

}
