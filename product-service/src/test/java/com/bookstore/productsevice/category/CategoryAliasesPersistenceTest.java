package com.bookstore.productsevice.category;

import com.bookstore.productsevice.category.dto.CategoryResponse;
import com.bookstore.productsevice.model.Category;
import org.bson.Document;
import org.junit.Before;
import org.junit.Test;
import org.springframework.data.mongodb.core.convert.MappingMongoConverter;
import org.springframework.data.mongodb.core.convert.MongoCustomConversions;
import org.springframework.data.mongodb.core.convert.NoOpDbRefResolver;
import org.springframework.data.mongodb.core.mapping.MongoMappingContext;

import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;

public class CategoryAliasesPersistenceTest {

    private MappingMongoConverter converter;

    @Before
    public void setUp() {
        MongoCustomConversions conversions = MongoCustomConversions.create(adapter -> {});
        MongoMappingContext context = new MongoMappingContext();
        context.setSimpleTypeHolder(conversions.getSimpleTypeHolder());
        converter = new MappingMongoConverter(NoOpDbRefResolver.INSTANCE, context);
        converter.setCustomConversions(conversions);
        converter.afterPropertiesSet();
    }

    @Test
    public void aliases_roundTripAsMongoStringArray() {
        Category category = new Category().setId("carpentry").setDisplayName("Carpentry")
                .setDescription("Woodwork").setAliases(List.of("woodworking", "cabinet repair"));
        Document document = new Document();

        converter.write(category, document);
        Category restored = converter.read(Category.class, document);

        assertThat(document.getList("aliases", String.class)).containsExactly("woodworking", "cabinet repair");
        assertThat(restored.getAliases()).containsExactlyElementsOf(category.getAliases());
        assertThat(restored.getId()).isEqualTo("carpentry");
    }

    @Test
    public void legacyMongoDocumentsWithoutAliases_needNoMigration() {
        Category restored = converter.read(Category.class, new Document("_id", "carpentry")
                .append("displayName", "Carpentry").append("description", "Woodwork"));

        assertThat(restored.getAliases()).isEmpty();
        assertThat(CategoryResponse.from(restored).aliases()).isEmpty();
    }
}
