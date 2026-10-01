package com.bookstore.productsevice.category;

import com.bookstore.productsevice.model.Category;
import com.bookstore.productsevice.repository.CategoryRepository;
import org.junit.Before;
import org.junit.Test;
import org.springframework.dao.DataAccessResourceFailureException;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;
import static org.junit.Assert.assertThrows;
import static org.mockito.Mockito.*;

public class CategoryCatalogServiceTest {

    private CategoryRepository repository;
    private CategoryCatalogService service;

    @Before
    public void setUp() {
        repository = mock(CategoryRepository.class);
        service = new CategoryCatalogService(new CategoryService(repository));
    }

    @Test
    public void catalog_usesCanonicalUtf8MetadataForDigestAndPayload() throws Exception {
        when(repository.findAllByOrderByDisplayNameAsc()).thenReturn(List.of(
                category("carpentry", "Carpentry", "R\u00e9paration du bois.")
                        .setAliases(List.of("woodworking", "cabinet repair"))));
        String canonical = "[{\"id\":\"carpentry\",\"displayName\":\"Carpentry\","
                + "\"description\":\"R\u00e9paration du bois.\",\"aliases\":[\"cabinet repair\",\"woodworking\"]}]";
        String expectedVersion = "sha256:" + HexFormat.of().formatHex(
                MessageDigest.getInstance("SHA-256").digest(canonical.getBytes(StandardCharsets.UTF_8)));

        CategoryCatalogService.CatalogSnapshot snapshot = service.getCatalog();

        assertThat(snapshot.schemaVersion()).isEqualTo(1);
        assertThat(snapshot.catalogVersion()).isEqualTo(expectedVersion);
        assertThat(service.serialize(snapshot)).isEqualTo(
                "{\"schemaVersion\":1,\"catalogVersion\":\"" + expectedVersion
                        + "\",\"categories\":" + canonical + "}");
        verify(repository).findAllByOrderByDisplayNameAsc();
        verifyNoMoreInteractions(repository);
    }

    @Test
    public void catalog_sortingIsByIdAndAliasNotDatabaseOrDisplayNameOrder() {
        Category first = category("electrical", "Alpha", "Wiring").setAliases(List.of("wiring", "lighting"));
        Category second = category("carpentry", "Zulu", "Woodwork");
        when(repository.findAllByOrderByDisplayNameAsc()).thenReturn(List.of(first, second));

        CategoryCatalogService.CatalogSnapshot original = service.getCatalog();
        first.setAliases(List.of("lighting", "wiring"));
        when(repository.findAllByOrderByDisplayNameAsc()).thenReturn(List.of(second, first));
        CategoryCatalogService.CatalogSnapshot reordered = service.getCatalog();

        assertThat(original.catalogVersion()).isEqualTo(reordered.catalogVersion());
        assertThat(original.categories()).extracting("id").containsExactly("carpentry", "electrical");
        assertThat(original.categories().get(1).aliases()).containsExactly("lighting", "wiring");
        assertThat(service.serialize(original)).isEqualTo(service.serialize(reordered));
    }

    @Test
    public void catalog_everyPublicMetadataEditChangesVersionButAuditFieldsDoNot() {
        Category category = category("carpentry", "Carpentry", "Woodwork");
        when(repository.findAllByOrderByDisplayNameAsc()).thenReturn(List.of(category));
        String baseline = service.getCatalog().catalogVersion();

        category.setCreatedAt(Instant.now()).setUpdatedAt(Instant.now())
                .setCreatedBy("creator").setUpdatedBy("editor").setNormalizedDisplayName("internal");
        assertThat(service.getCatalog().catalogVersion()).isEqualTo(baseline);
        category.setId("joinery");
        assertThat(service.getCatalog().catalogVersion()).isNotEqualTo(baseline);
        category.setId("carpentry").setDisplayName("Fine Carpentry");
        assertThat(service.getCatalog().catalogVersion()).isNotEqualTo(baseline);
        category.setDisplayName("Carpentry").setDescription("Cabinet construction and repair");
        assertThat(service.getCatalog().catalogVersion()).isNotEqualTo(baseline);
        category.setDescription("Woodwork").setAliases(List.of("woodworking"));
        assertThat(service.getCatalog().catalogVersion()).isNotEqualTo(baseline);
    }

    @Test
    public void catalog_snapshotIsImmutableAndDoesNotExposeAuditMetadata() {
        List<String> aliases = new ArrayList<>(List.of("woodworking"));
        Category category = category("carpentry", "Carpentry", "Woodwork").setAliases(aliases);
        when(repository.findAllByOrderByDisplayNameAsc()).thenReturn(List.of(category));

        CategoryCatalogService.CatalogSnapshot snapshot = service.getCatalog();
        category.setDescription("Changed");
        aliases.add("cabinet repair");

        assertThat(snapshot.categories().getFirst().description()).isEqualTo("Woodwork");
        assertThat(snapshot.categories().getFirst().aliases()).containsExactly("woodworking");
        assertThrows(UnsupportedOperationException.class, () -> snapshot.categories().clear());
        assertThrows(UnsupportedOperationException.class, () -> snapshot.categories().getFirst().aliases().clear());
        assertThat(service.serialize(snapshot)).doesNotContain(
                "createdAt", "updatedAt", "createdBy", "updatedBy", "normalizedDisplayName");
    }

    @Test
    public void emptyCatalog_hasStableEmptyArrayVersion() {
        when(repository.findAllByOrderByDisplayNameAsc()).thenReturn(List.of());

        CategoryCatalogService.CatalogSnapshot snapshot = service.getCatalog();

        assertThat(snapshot.catalogVersion()).isEqualTo(
                "sha256:4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945");
        assertThat(service.serialize(snapshot)).endsWith("\"categories\":[]}");
    }

    @Test
    public void unavailableStorage_isNotAnEmptySuccessfulCatalog() {
        when(repository.findAllByOrderByDisplayNameAsc())
                .thenThrow(new DataAccessResourceFailureException("private-storage-sentinel"));

        CategoryCatalogUnavailableException exception = assertThrows(
                CategoryCatalogUnavailableException.class, service::getCatalog);

        assertThat(exception.getMessage()).isEqualTo("Category catalog is temporarily unavailable.");
        assertThat(exception.getMessage()).doesNotContain("private-storage-sentinel");
    }

    private Category category(String id, String name, String description) {
        return new Category().setId(id).setDisplayName(name).setDescription(description);
    }
}
