package com.bookstore.authentication.model;


import com.fasterxml.jackson.annotation.JsonIgnore;
import lombok.Getter;
import lombok.NoArgsConstructor;
import lombok.Setter;
import org.springframework.data.annotation.Id;

@Getter
@Setter
@NoArgsConstructor
public class Lookup {

    @Id
    @JsonIgnore
    private String id;

    private String code;
    private String label;
    private String groupId;


    public Lookup(String code, String label, String groupId) {
        this.code = code;
        this.label = label;
        this.groupId = groupId;
    }
}
