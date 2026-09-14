package com.bookstore.productsevice.location;

import com.bookstore.productsevice.model.TaskMaster;
import org.junit.Test;
import org.springframework.data.mongodb.core.MongoTemplate;
import org.springframework.data.mongodb.core.index.IndexDefinition;
import org.springframework.data.mongodb.core.index.IndexOperations;
import org.springframework.data.mongodb.core.query.Query;

import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.times;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

public class LocationIndexInitializerTest {

    @Test
    public void run_legacyDocumentsExist_failsBeforeCreatingIndexes() {
        MongoTemplate mongoTemplate = mock(MongoTemplate.class);
        when(mongoTemplate.collectionExists(TaskMaster.class)).thenReturn(true);
        when(mongoTemplate.count(any(Query.class), org.mockito.ArgumentMatchers.eq(TaskMaster.class)))
                .thenReturn(2L);

        LocationIndexInitializer initializer = new LocationIndexInitializer(mongoTemplate);

        assertThatThrownBy(initializer::run)
                .isInstanceOf(IllegalStateException.class)
                .hasMessageContaining("Recreate the products database");
        verify(mongoTemplate, never()).indexOps(TaskMaster.class);
    }

    @Test
    public void run_cleanDatabase_createsLocationIndexes() {
        MongoTemplate mongoTemplate = mock(MongoTemplate.class);
        IndexOperations indexOperations = mock(IndexOperations.class);
        when(mongoTemplate.collectionExists(TaskMaster.class)).thenReturn(false);
        when(mongoTemplate.indexOps(TaskMaster.class)).thenReturn(indexOperations);

        new LocationIndexInitializer(mongoTemplate).run();

        verify(indexOperations, times(4)).ensureIndex(any(IndexDefinition.class));
    }
}
