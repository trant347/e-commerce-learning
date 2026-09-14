package com.bookstore.productsevice.repository;

import com.bookstore.productsevice.location.LocationSearchCriteria;
import com.bookstore.productsevice.model.TaskMaster;
import org.junit.Before;
import org.junit.Test;
import org.mockito.ArgumentCaptor;
import org.springframework.data.mongodb.core.MongoTemplate;
import org.springframework.data.mongodb.core.query.Query;

import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

public class TaskMasterSearchRepositoryImplTest {

    private MongoTemplate mongoTemplate;
    private TaskMasterSearchRepositoryImpl repository;

    @Before
    public void setUp() {
        mongoTemplate = mock(MongoTemplate.class);
        repository = new TaskMasterSearchRepositoryImpl(mongoTemplate);
        when(mongoTemplate.find(org.mockito.ArgumentMatchers.any(Query.class), eq(TaskMaster.class)))
                .thenReturn(List.of());
    }

    @Test
    public void findByLocation_stateSearch_usesNormalizedStateField() {
        repository.findByLocation(
                new LocationSearchCriteria(LocationSearchCriteria.MatchMode.STATE, null, "IL"),
                null);

        Query query = captureQuery();
        assertThat(query.getQueryObject().getString("locationStateCode")).isEqualTo("IL");
    }

    @Test
    public void searchWithFilters_ambiguousName_matchesCityOrState() {
        repository.searchWithFilters(
                "carpentry",
                new LocationSearchCriteria(
                        LocationSearchCriteria.MatchMode.CITY_OR_STATE,
                        "new york",
                        "NY"),
                null,
                null,
                null,
                10);

        Query query = captureQuery();
        assertThat(query.getQueryObject().toJson())
                .contains("\"jobCategories\": \"carpentry\"")
                .contains("\"$or\"")
                .contains("\"locationCity\": \"new york\"")
                .contains("\"locationStateCode\": \"NY\"");
        assertThat(query.getLimit()).isEqualTo(10);
    }

    private Query captureQuery() {
        ArgumentCaptor<Query> captor = ArgumentCaptor.forClass(Query.class);
        verify(mongoTemplate).find(captor.capture(), eq(TaskMaster.class));
        return captor.getValue();
    }
}
