package com.bookstore.authentication.repository;

import com.bookstore.authentication.model.User;
import org.springframework.data.mongodb.repository.MongoRepository;

import java.util.List;

public interface UserRepository extends MongoRepository<User, String> {

    public User findByUserId(String id);
    public List<User> findAllByEmail(String email);
    public List<User> findAllByUsername(String username);
    public List<User> findAll();
    public User findFirstByUsername(String username);
    public User findFirstByEmail(String email);

}
