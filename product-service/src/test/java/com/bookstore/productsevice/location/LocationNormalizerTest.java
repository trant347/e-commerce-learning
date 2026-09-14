package com.bookstore.productsevice.location;

import org.junit.Test;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

public class LocationNormalizerTest {

    private final LocationNormalizer normalizer = new LocationNormalizer();

    @Test
    public void normalizeForWrite_canonicalizesCityAndFullStateName() {
        NormalizedLocation result = normalizer.normalizeForWrite("  chicago , illinois ");

        assertThat(result.displayLocation()).isEqualTo("Chicago, IL");
        assertThat(result.city()).isEqualTo("chicago");
        assertThat(result.stateCode()).isEqualTo("IL");
    }

    @Test
    public void normalizeForWrite_rejectsCityWithoutState() {
        assertThatThrownBy(() -> normalizer.normalizeForWrite("Chicago"))
                .isInstanceOf(LocationValidationException.class)
                .hasMessageContaining("City, ST");
    }

    @Test
    public void normalizeForSearch_acceptsStateCode() {
        LocationSearchCriteria result = normalizer.normalizeForSearch("il");

        assertThat(result.matchMode()).isEqualTo(LocationSearchCriteria.MatchMode.STATE);
        assertThat(result.stateCode()).isEqualTo("IL");
        assertThat(result.cacheKey()).isEqualTo("state:IL");
    }

    @Test
    public void normalizeForSearch_treatsFullStateNameAsAmbiguous() {
        LocationSearchCriteria result = normalizer.normalizeForSearch("New York");

        assertThat(result.matchMode()).isEqualTo(LocationSearchCriteria.MatchMode.CITY_OR_STATE);
        assertThat(result.city()).isEqualTo("new york");
        assertThat(result.stateCode()).isEqualTo("NY");
    }

    @Test
    public void normalizeForSearch_acceptsCityAndState() {
        LocationSearchCriteria result = normalizer.normalizeForSearch("Chicago, Illinois");

        assertThat(result.matchMode()).isEqualTo(LocationSearchCriteria.MatchMode.CITY_AND_STATE);
        assertThat(result.city()).isEqualTo("chicago");
        assertThat(result.stateCode()).isEqualTo("IL");
    }

    @Test
    public void normalizeForSearch_rejectsUnsupportedState() {
        assertThatThrownBy(() -> normalizer.normalizeForSearch("Chicago, ZZ"))
                .isInstanceOf(LocationValidationException.class)
                .hasMessageContaining("valid US state");
    }
}
