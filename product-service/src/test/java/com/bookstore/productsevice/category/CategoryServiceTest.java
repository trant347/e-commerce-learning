package com.bookstore.productsevice.category;

import com.bookstore.productsevice.model.Category;
import com.bookstore.productsevice.repository.CategoryRepository;
import org.junit.Before;
import org.junit.Test;
import org.springframework.dao.DuplicateKeyException;

import java.time.Instant;
import java.util.List;
import java.util.Optional;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

public class CategoryServiceTest {

    private static final Instant NOW = Instant.parse("2026-09-22T20:00:00Z");

    private CategoryRepository repository;
    private CategoryService service;

    @Before
    public void setUp() {
        repository = mock(CategoryRepository.class);
        service = new CategoryService(repository, () -> NOW);
        when(repository.save(any(Category.class)))
                .thenAnswer(invocation -> invocation.getArgument(0));
    }

    @Test
    public void createCategory_normalizesAndSetsAuditFields() {
        Category result = service.createCategory(
                "  Furniture-Assembly ",
                "  Furniture   Assembly ",
                "  Assembly and installation.  ",
                " admin ");

        assertThat(result.getId()).isEqualTo("furniture-assembly");
        assertThat(result.getDisplayName()).isEqualTo("Furniture Assembly");
        assertThat(result.getNormalizedDisplayName()).isEqualTo("furniture assembly");
        assertThat(result.getDescription()).isEqualTo("Assembly and installation.");
        assertThat(result.getCreatedAt()).isEqualTo(NOW);
        assertThat(result.getCreatedBy()).isEqualTo("admin");
        assertThat(result.getUpdatedAt()).isEqualTo(NOW);
        assertThat(result.getUpdatedBy()).isEqualTo("admin");
    }

    @Test
    public void createCategory_rejectsInvalidId() {
        assertThatThrownBy(() -> service.createCategory(
                "Furniture Assembly",
                "Furniture Assembly",
                "Assembly and installation.",
                "admin"))
                .isInstanceOf(CategoryValidationException.class)
                .extracting("errorCode")
                .isEqualTo("invalid_category");

        verify(repository, never()).save(any());
    }

    @Test
    public void createCategory_rejectsNormalizedIdCollision() {
        when(repository.existsById("carpentry")).thenReturn(true);

        assertThatThrownBy(() -> service.createCategory(
                " Carpentry ",
                "Carpentry",
                "Wood construction and repair.",
                "admin"))
                .isInstanceOf(CategoryConflictException.class)
                .extracting("errorCode", "field")
                .containsExactly("duplicate_category_id", "id");
    }

    @Test
    public void createCategory_rejectsNormalizedDisplayNameCollision() {
        when(repository.existsByNormalizedDisplayName("furniture assembly")).thenReturn(true);

        assertThatThrownBy(() -> service.createCategory(
                "flat-pack-assembly",
                " FURNITURE   ASSEMBLY ",
                "Assembly and installation.",
                "admin"))
                .isInstanceOf(CategoryConflictException.class)
                .extracting("errorCode", "field")
                .containsExactly("duplicate_category_display_name", "displayName");
    }

    @Test
    public void createCategory_translatesConcurrentDuplicateKey() {
        when(repository.save(any(Category.class)))
                .thenThrow(new DuplicateKeyException("duplicate normalized display name"));

        assertThatThrownBy(() -> service.createCategory(
                "carpentry",
                "Carpentry",
                "Wood construction and repair.",
                "admin"))
                .isInstanceOf(CategoryConflictException.class)
                .extracting("errorCode")
                .isEqualTo("duplicate_category_display_name");
    }

    @Test
    public void updateCategory_preservesIdAndCreationAudit() {
        Category existing = category(
                "carpentry",
                "Carpentry",
                "carpentry",
                "Old description")
                .setCreatedAt(Instant.parse("2026-01-01T00:00:00Z"))
                .setCreatedBy("original-admin");
        when(repository.findById("carpentry")).thenReturn(Optional.of(existing));

        Category result = service.updateCategory(
                " CARPENTRY ",
                " Fine   Carpentry ",
                " Custom wood construction. ",
                "editor");

        assertThat(result.getId()).isEqualTo("carpentry");
        assertThat(result.getDisplayName()).isEqualTo("Fine Carpentry");
        assertThat(result.getNormalizedDisplayName()).isEqualTo("fine carpentry");
        assertThat(result.getDescription()).isEqualTo("Custom wood construction.");
        assertThat(result.getCreatedBy()).isEqualTo("original-admin");
        assertThat(result.getCreatedAt()).isEqualTo(Instant.parse("2026-01-01T00:00:00Z"));
        assertThat(result.getUpdatedAt()).isEqualTo(NOW);
        assertThat(result.getUpdatedBy()).isEqualTo("editor");
        verify(repository).existsByNormalizedDisplayName("fine carpentry");
    }

    @Test
    public void updateCategory_translatesConcurrentDisplayNameCollision() {
        Category existing = category(
                "carpentry",
                "Carpentry",
                "carpentry",
                "Old description");
        when(repository.findById("carpentry")).thenReturn(Optional.of(existing));
        when(repository.save(existing))
                .thenThrow(new DuplicateKeyException("duplicate normalized display name"));

        assertThatThrownBy(() -> service.updateCategory(
                "carpentry",
                "Fine Carpentry",
                "Custom wood construction.",
                "editor"))
                .isInstanceOf(CategoryConflictException.class)
                .extracting("errorCode", "field")
                .containsExactly("duplicate_category_display_name", "displayName");
    }

    @Test
    public void getCategories_usesRepositoryDisplayNameOrdering() {
        List<Category> categories = List.of(
                category("carpentry", "Carpentry", "carpentry", "Woodwork"),
                category("plumbing", "Plumbing", "plumbing", "Pipework"));
        when(repository.findAllByOrderByDisplayNameAsc()).thenReturn(categories);

        assertThat(service.getCategories()).containsExactlyElementsOf(categories);
    }

    @Test
    public void normalizeAndValidateCategoryIds_normalizesDeduplicatesAndPreservesOrder() {
        when(repository.findAllById(any())).thenReturn(List.of(
                category("plumbing", "Plumbing", "plumbing", "Pipework"),
                category("carpentry", "Carpentry", "carpentry", "Woodwork")));

        List<String> result = service.normalizeAndValidateCategoryIds(
                List.of(" Plumbing ", "carpentry", "PLUMBING"));

        assertThat(result).containsExactly("plumbing", "carpentry");
    }

    @Test
    public void normalizeAndValidateCategoryIds_rejectsUnknownIds() {
        when(repository.findAllById(any())).thenReturn(List.of(
                category("plumbing", "Plumbing", "plumbing", "Pipework")));

        assertThatThrownBy(() -> service.normalizeAndValidateCategoryIds(
                List.of("plumbing", "unknown-service")))
                .isInstanceOf(CategoryValidationException.class)
                .hasMessage("Unknown categories: unknown-service.")
                .extracting("errorCode")
                .isEqualTo("unknown_category");
    }

    @Test
    public void normalizeAndValidateCategoryIds_rejectsEmptySelection() {
        assertThatThrownBy(() -> service.normalizeAndValidateCategoryIds(List.of()))
                .isInstanceOf(CategoryValidationException.class)
                .extracting("errorCode")
                .isEqualTo("invalid_category");
    }

    @Test
    public void getCategory_rejectsUnknownId() {
        when(repository.findById("carpentry")).thenReturn(Optional.empty());

        assertThatThrownBy(() -> service.getCategory("carpentry"))
                .isInstanceOf(CategoryNotFoundException.class)
                .hasMessage("Category 'carpentry' was not found.");
    }

    private Category category(String id,
                              String displayName,
                              String normalizedDisplayName,
                              String description) {
        return new Category()
                .setId(id)
                .setDisplayName(displayName)
                .setNormalizedDisplayName(normalizedDisplayName)
                .setDescription(description);
    }
}
