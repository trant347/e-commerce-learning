package com.mocks;

import com.bookstore.authentication.model.User;
import com.bookstore.authentication.repository.UserRepository;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Profile;
import org.springframework.data.domain.Example;
import org.springframework.data.domain.Page;
import org.springframework.data.domain.Pageable;
import org.springframework.data.domain.Sort;

import java.util.ArrayList;
import java.util.List;
import java.util.Optional;

@Profile("test")
@org.springframework.context.annotation.Configuration
public class MockConfiguration {

    private List<User> availableUsers = new ArrayList<>();

    public MockConfiguration() {
        User tony = new User();
        tony.setEmail("tony@gmail.com");
        tony.setUsername("tony");
        availableUsers.add(tony);
    }


    @Bean
    public UserRepository userRepository() {
        return new UserRepository() {
            @Override
            public User findByUserId(String id) {
                return null;
            }

            @Override
            public List<User> findAllByEmail(String email) {
                if(email.equals(availableUsers.get(0).getEmail())) {
                    return availableUsers;
                }
                return new ArrayList<>();
            }

            @Override
            public List<User> findAllByUsername(String username) {
                if(username.equals(availableUsers.get(0).getUsername())) {
                    return availableUsers;
                }
                return new ArrayList<>();
            }

            @Override
            public List<User> findAll() {
                return null;
            }

            @Override
            public User findFirstByUsername(String username) {
                return null;
            }

            @Override
            public User findFirstByEmail(String email) {
                return null;
            }

            @Override
            public <S extends User> List<S> saveAll(Iterable<S> iterable) {
                return null;
            }

            @Override
            public List<User> findAll(Sort sort) {
                return null;
            }

            @Override
            public <S extends User> S insert(S s) {
                return null;
            }

            @Override
            public <S extends User> List<S> insert(Iterable<S> iterable) {
                return null;
            }

            @Override
            public <S extends User> List<S> findAll(Example<S> example) {
                return null;
            }

            @Override
            public <S extends User> List<S> findAll(Example<S> example, Sort sort) {
                return null;
            }

            @Override
            public Page<User> findAll(Pageable pageable) {
                return null;
            }

            @Override
            public <S extends User> S save(S s) {
                return null;
            }

            @Override
            public Optional<User> findById(String s) {
                return Optional.empty();
            }

            @Override
            public boolean existsById(String s) {
                return false;
            }

            @Override
            public Iterable<User> findAllById(Iterable<String> iterable) {
                return null;
            }

            @Override
            public long count() {
                return 0;
            }

            @Override
            public void deleteById(String s) {

            }

            @Override
            public void delete(User user) {

            }

            @Override
            public void deleteAll(Iterable<? extends User> iterable) {

            }

            @Override
            public void deleteAll() {

            }

            @Override
            public <S extends User> Optional<S> findOne(Example<S> example) {
                return Optional.empty();
            }

            @Override
            public <S extends User> Page<S> findAll(Example<S> example, Pageable pageable) {
                return null;
            }

            @Override
            public <S extends User> long count(Example<S> example) {
                return 0;
            }

            @Override
            public <S extends User> boolean exists(Example<S> example) {
                return false;
            }
        };
    }
}
