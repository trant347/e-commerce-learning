package com.bookstore.productsevice.model;

import com.fasterxml.jackson.annotation.JsonIgnore;
import lombok.Data;
import lombok.experimental.Accessors;
import org.springframework.data.annotation.Id;
import org.springframework.data.mongodb.core.mapping.Document;

import java.io.Serializable;
import java.time.Instant;

@Document(collection = "categories")
@Data
@Accessors(chain = true)
public class Category implements Serializable {

    private static final long serialVersionUID = 1L;

    @Id
    private String id;

    private String displayName;

    @JsonIgnore
    private String normalizedDisplayName;

    private String description;

    @JsonIgnore
    private Instant createdAt;

    @JsonIgnore
    private String createdBy;

    @JsonIgnore
    private Instant updatedAt;

    @JsonIgnore
    private String updatedBy;
}
