package com.bookstore.authentication.repository;


import com.bookstore.authentication.model.Lookup;
import org.springframework.data.mongodb.repository.MongoRepository;

import java.util.List;

public interface LookupRepository extends MongoRepository<Lookup, String> {
    public List<Lookup> findAllByGroupId(String groupId);
    public List<Lookup> deleteAllByGroupId(String groupId);
}
