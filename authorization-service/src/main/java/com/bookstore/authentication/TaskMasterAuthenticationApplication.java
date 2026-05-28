package com.bookstore.authentication;

import com.bookstore.authentication.encoders.PasswordEncoder;
import com.bookstore.authentication.encoders.PasswordEncoderImpl;
import com.bookstore.authentication.model.Country;
import com.bookstore.authentication.model.Gender;
import com.bookstore.authentication.model.User;
import com.bookstore.authentication.repository.LookupRepository;
import com.bookstore.authentication.repository.UserRepository;
import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.CommandLineRunner;
import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.cloud.client.discovery.EnableDiscoveryClient;
import org.springframework.context.annotation.Bean;
import org.springframework.core.io.ClassPathResource;

import java.io.InputStream;
import java.util.List;

@SpringBootApplication
@EnableDiscoveryClient
public class TaskMasterAuthenticationApplication {


    Logger logger = LoggerFactory.getLogger(TaskMasterAuthenticationApplication.class);

    @Autowired
    UserRepository userRepository;

    @Autowired
    LookupRepository lookupRepository;

    @Autowired
    PasswordEncoder passwordEncoder;



    public static void main(String[] args) {
        SpringApplication.run(TaskMasterAuthenticationApplication.class);
    }


    @Bean
    public CommandLineRunner createDummyDatabase() {
        return new CommandLineRunner() {
            @Override
            public void run(String... args) throws Exception {

                List<User> users = userRepository.findAllByEmail("admin@gmail.com");
                users.stream().forEach(user -> userRepository.delete(user));

                User user = new User();

                user.setEncrytedPassword(passwordEncoder.encode("admin"))
                        .setEmail("admin@gmail.com")
                        .setUsername("admin")
                        .setFirstName("admin")
                        .setLastName("admin")
                        .setRole("ADMIN");

                User createdUser = userRepository.save(user);
                logger.info("Dummy user has id as "+createdUser.getUserId());

                lookupRepository.deleteAllByGroupId("country");
                lookupRepository.deleteAllByGroupId("gender");

                lookupRepository.save(new Country("CA", "Canada", "North America"));
                lookupRepository.save(new Country("US", "United States", "North America"));
                lookupRepository.save(new Country("VN", "Vietnam", "South East Asia"));
                lookupRepository.save(new Country("DE", "Germany", "Europe"));

                lookupRepository.save(new Gender("M", "Male"));
                lookupRepository.save(new Gender("F", "Female"));
                lookupRepository.save(new Gender("U", "Unknown"));

            }
        };
    }

    @Bean
    public CommandLineRunner seedTaskMasterOwners() {
        return args -> {
            ObjectMapper objectMapper = new ObjectMapper();
            objectMapper.configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false);

            ClassPathResource seedResource = new ClassPathResource("seed/users.json");
            if (!seedResource.exists()) {
                logger.info("seed/users.json not found; skipping task master owner seed.");
                return;
            }

            SeedUsersPayload payload;
            try (InputStream in = seedResource.getInputStream()) {
                payload = objectMapper.readValue(in, SeedUsersPayload.class);
            }
            if (payload == null || payload.users == null || payload.users.isEmpty()) {
                logger.warn("seed/users.json contained no users.");
                return;
            }

            String rawPassword = payload.defaultPassword == null || payload.defaultPassword.isBlank()
                    ? "password" : payload.defaultPassword;
            String encoded = passwordEncoder.encode(rawPassword);

            int created = 0, skipped = 0;
            for (SeedUserEntry entry : payload.users) {
                if (entry == null || entry.username == null || entry.username.isBlank()) continue;
                List<User> existing = userRepository.findAllByUsername(entry.username);
                if (!existing.isEmpty()) { skipped++; continue; }

                User u = new User()
                        .setUsername(entry.username)
                        .setFirstName(entry.firstName)
                        .setLastName(entry.lastName)
                        .setEmail(entry.email)
                        .setRole("USER")
                        .setEncrytedPassword(encoded);
                userRepository.save(u);
                created++;
            }
            logger.info("Seeded task master owners: {} created, {} already existed (password='{}').",
                    created, skipped, rawPassword);
        };
    }

    private static class SeedUsersPayload {
        public String defaultPassword;
        public List<SeedUserEntry> users;
    }

    private static class SeedUserEntry {
        public String username;
        public String firstName;
        public String lastName;
        public String email;
    }
}
