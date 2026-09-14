package com.bookstore.productsevice.location;

public record NormalizedLocation(
        String displayLocation,
        String city,
        String stateCode) {
}
