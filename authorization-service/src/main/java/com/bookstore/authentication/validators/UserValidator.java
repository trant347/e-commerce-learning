package com.bookstore.authentication.validators;

import com.bookstore.authentication.exceptions.EmailNotAvailableException;
import com.bookstore.authentication.exceptions.InvalidEmailException;
import com.bookstore.authentication.exceptions.UsernameNotAvailableException;
import com.bookstore.authentication.model.User;
import com.bookstore.authentication.repository.UserRepository;
import org.apache.commons.validator.routines.EmailValidator;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.stereotype.Component;

import java.util.List;


@Component
public class UserValidator {

    private UserRepository userRepository;

    @Autowired
    public UserValidator(UserRepository userRepository) {
        this.userRepository = userRepository;
    }

    private EmailValidator emailValidator = EmailValidator.getInstance();




    public void validate(User user) throws Exception {


        if(!emailValidator.isValid(user.getEmail())) {
            throw new InvalidEmailException(user.getEmail());
        }


        List<User> existingUserWithSameEmail = userRepository.findAllByEmail(user.getEmail());

        if(existingUserWithSameEmail.size() > 0) {
            throw new EmailNotAvailableException(user.getEmail());
        }

        List<User> existingUserWithSameUsername = userRepository.findAllByUsername(user.getUsername());
        if(existingUserWithSameUsername.size() > 0) {
            throw new UsernameNotAvailableException(user.getUsername());
        }

    }



}
