package com.bookstore.productsevice;

import org.testcontainers.containers.DockerComposeContainer;

import java.io.File;

public class StartStopContainers {
    public static DockerComposeContainer startExternalServices() {
        return new DockerComposeContainer(
                new File("src/test/resources/docker-compose-test.yml")
        ).withExposedService("mongodb_1",27017)
                .withLocalCompose(true);
    }
}
