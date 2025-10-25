package com.bookstore.authentication;

import com.bookstore.authentication.encoders.PasswordEncoder;
import com.bookstore.authentication.encoders.PasswordEncoderImpl;
import com.bookstore.authentication.model.Country;
import com.bookstore.authentication.model.Gender;
import com.bookstore.authentication.model.User;
import com.bookstore.authentication.repository.LookupRepository;
import com.bookstore.authentication.repository.UserRepository;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.CommandLineRunner;
import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.cloud.client.discovery.EnableDiscoveryClient;
import org.springframework.context.annotation.Bean;

import java.util.List;

@SpringBootApplication
@EnableDiscoveryClient
public class BookstoreAuthenticationApplication {


    Logger logger = LoggerFactory.getLogger(BookstoreAuthenticationApplication.class);

    @Autowired
    UserRepository userRepository;

    @Autowired
    LookupRepository lookupRepository;

    @Autowired
    PasswordEncoder passwordEncoder;



    public static void main(String[] args) {
        SpringApplication.run(BookstoreAuthenticationApplication.class);
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
                        .setRole("admin");

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
}
