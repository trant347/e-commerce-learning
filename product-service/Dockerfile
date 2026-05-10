FROM maven:3.8-eclipse-temurin-11-alpine as builder

WORKDIR /app

COPY pom.xml ./
COPY ./src ./src/
COPY ./static ./static/


RUN mvn package -DskipTests

FROM eclipse-temurin:11-jre-alpine

ARG version
WORKDIR /app
COPY --from=builder /app/target/product-service-${version}.jar ./product-service.jar
COPY --from=builder /app/static ./static

ENV JAVA_TOOL_OPTIONS -agentlib:jdwp=transport=dt_socket,address=8000,server=y,suspend=n

EXPOSE 8080 8000

ENTRYPOINT ["sh", "-c"]
CMD ["java -jar product-service.jar"]