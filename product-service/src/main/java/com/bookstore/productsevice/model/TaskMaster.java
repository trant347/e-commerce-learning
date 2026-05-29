package com.bookstore.productsevice.model;

import lombok.Data;
import lombok.Getter;
import lombok.Setter;
import lombok.experimental.Accessors;
import org.springframework.data.annotation.Id;
import org.springframework.data.mongodb.core.index.Indexed;
import org.springframework.data.mongodb.core.mapping.Document;

import jakarta.validation.constraints.NotBlank;

@Document(collection = "taskmaster")
@Data
@Getter
@Setter
@Accessors(chain = true)
public class TaskMaster {

    @Id
    private String id;

    private String name;
    private int age;
    private String photo;
    private String location;
    private double rating;
    private String[] jobCategories;
    private String description;
    private double hourlyRateUsd;

    /**
     * The username of the registered user who owns this task master profile.
     * Required: every task master must have an owner (a user, however, is not
     * required to be a task master). Set when an application is accepted.
     * Unique index enforces one profile per user; the index is non-sparse so
     * a missing/null owner would also collide and be rejected by Mongo.
     */
    @Indexed(unique = true)
    @NotBlank(message = "ownerUsername is required")
    private String ownerUsername;

    public TaskMaster() {}

    public TaskMaster(String name, int age, String photo, String location, double rating, 
                      String[] jobCategories, String description, double hourlyRateUsd) {
        this.name = name;
        this.age = age;
        this.photo = photo;
        this.location = location;
        this.rating = rating;
        this.jobCategories = jobCategories;
        this.description = description;
        this.hourlyRateUsd = hourlyRateUsd;
    }

    public static class Builder {
        private String name;
        private int age;
        private String photo;
        private String location;
        private double rating;
        private String[] jobCategories;
        private String description;
        private double hourlyRateUsd;

        public Builder setName(String name) {
            this.name = name;
            return this;
        }

        public Builder setAge(int age) {
            this.age = age;
            return this;
        }

        public Builder setPhoto(String photo) {
            this.photo = photo;
            return this;
        }

        public Builder setLocation(String location) {
            this.location = location;
            return this;
        }

        public Builder setRating(double rating) {
            this.rating = rating;
            return this;
        }

        public Builder setJobCategories(String[] jobCategories) {
            this.jobCategories = jobCategories;
            return this;
        }

        public Builder setDescription(String description) {
            this.description = description;
            return this;
        }

        public Builder setHourlyRateUsd(double hourlyRateUsd) {
            this.hourlyRateUsd = hourlyRateUsd;
            return this;
        }

        public Builder() {}

        public TaskMaster build() {
            return new TaskMaster(name, age, photo, location, rating, jobCategories, description, hourlyRateUsd);
        }
    }
}
