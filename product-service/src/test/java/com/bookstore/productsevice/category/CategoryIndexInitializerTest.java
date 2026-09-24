package com.bookstore.productsevice.category;

import com.bookstore.productsevice.model.Category;
import org.bson.Document;
import org.junit.Test;
import org.mockito.ArgumentCaptor;
import org.springframework.data.mongodb.core.MongoTemplate;
import org.springframework.data.mongodb.core.index.IndexDefinition;
import org.springframework.data.mongodb.core.index.IndexOperations;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

public class CategoryIndexInitializerTest {

    @Test
    public void run_ensuresUniqueNormalizedDisplayNameIndex() {
        MongoTemplate mongoTemplate = mock(MongoTemplate.class);
        IndexOperations indexOperations = mock(IndexOperations.class);
        when(mongoTemplate.indexOps(Category.class)).thenReturn(indexOperations);

        new CategoryIndexInitializer(mongoTemplate).run();

        ArgumentCaptor<IndexDefinition> indexCaptor =
                ArgumentCaptor.forClass(IndexDefinition.class);
        verify(indexOperations).ensureIndex(indexCaptor.capture());

        IndexDefinition index = indexCaptor.getValue();
        assertThat(index.getIndexKeys())
                .isEqualTo(new Document("normalizedDisplayName", 1));
        assertThat(index.getIndexOptions())
                .containsEntry("name", "category_display_name_unique")
                .containsEntry("unique", true);
    }
}
