package com.bookstore.productsevice.model;

import lombok.Data;
import lombok.Getter;
import lombok.Setter;
import lombok.experimental.Accessors;
import org.springframework.data.annotation.Id;
import org.springframework.data.mongodb.core.index.Indexed;
import org.springframework.data.mongodb.core.mapping.Document;

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
     * Set when an application is accepted. Indexed for efficient lookup during
     * profile queries and cascading cleanup on account deletion (via USER_DELETED Kafka event).
     * Unique + sparse: one user can own at most one profile; null values are excluded
     * from the index so legacy task masters without an owner do not conflict.
     */
    @Indexed(unique = true, sparse = true)
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
