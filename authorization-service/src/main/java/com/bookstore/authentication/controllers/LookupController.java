package com.bookstore.authentication.controllers;

import com.bookstore.authentication.model.Lookup;
import com.bookstore.authentication.repository.LookupRepository;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.http.ResponseEntity;
import org.springframework.stereotype.Controller;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;

import java.util.List;

@Controller
public class LookupController {

    @Autowired
    LookupRepository lookupRepository;

    @GetMapping("/lookup/{groupId}")
    public ResponseEntity<?> getLookups(@PathVariable String groupId) {

        List<Lookup> lookup = lookupRepository.findAllByGroupId(groupId);

        return ResponseEntity.ok(lookup);


    }
}
