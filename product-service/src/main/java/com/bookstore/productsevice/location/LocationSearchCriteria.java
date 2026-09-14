package com.bookstore.productsevice.location;

public record LocationSearchCriteria(
        MatchMode matchMode,
        String city,
        String stateCode) {

    public enum MatchMode {
        CITY,
        STATE,
        CITY_AND_STATE,
        CITY_OR_STATE
    }

    public String cacheKey() {
        return switch (matchMode) {
            case CITY -> "city:" + city;
            case STATE -> "state:" + stateCode;
            case CITY_AND_STATE -> "city-state:" + city + ":" + stateCode;
            case CITY_OR_STATE -> "city-or-state:" + city + ":" + stateCode;
        };
    }
}
