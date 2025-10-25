package com.bookstore.authentication.controllers;

import com.bookstore.authentication.configs.JwtConfig;
import com.bookstore.authentication.encoders.PasswordEncoder;
import com.bookstore.authentication.exceptions.AuthenticationException;
import com.bookstore.authentication.exceptions.InvalidUserIdException;
import com.bookstore.authentication.model.User;
import com.bookstore.authentication.repository.UserRepository;
import com.bookstore.authentication.utils.JwtTokenUtil;
import com.bookstore.authentication.validators.UserValidator;
import io.jsonwebtoken.Claims;
import io.jsonwebtoken.Jwts;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.security.authentication.AuthenticationManager;
import org.springframework.security.authentication.BadCredentialsException;
import org.springframework.security.authentication.DisabledException;
import org.springframework.security.authentication.UsernamePasswordAuthenticationToken;
import org.springframework.security.core.userdetails.UserDetails;
import org.springframework.security.core.userdetails.UserDetailsService;
import org.springframework.web.bind.annotation.CrossOrigin;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.PutMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RestController;

import javax.servlet.http.HttpServletResponse;
import java.util.List;
import java.util.Objects;


@RestController
@CrossOrigin
public class UserController {

    Logger logger = LoggerFactory.getLogger(UserController.class);

    @Autowired
    UserRepository userRepository;

    @Autowired
    PasswordEncoder passwordEncoder;

    @Autowired
    UserValidator userValidator;

    @Autowired
    @Qualifier("customJwtUserDetailsService")
    UserDetailsService userDetailsService;

    @Autowired
    private AuthenticationManager authenticationManager;

    @Autowired
    private JwtTokenUtil jwtTokenUtil;

    @Autowired
    private JwtConfig jwtConfig;


    @PostMapping(value="/register")
    public ResponseEntity<User> createUser(@RequestBody User user) throws Exception {

        userValidator.validate(user);

        user.setEncrytedPassword(passwordEncoder.encode(user.getPassword()));
        user.setPassword(null);

        if(user.getRole() == null) {
               user.setRole("user");
        }

        User createdUser = userRepository.save(user);
        return new ResponseEntity<>(createdUser, HttpStatus.OK);
    }

    @GetMapping(value="/{username}")
    public ResponseEntity<User> getUserByName(@PathVariable String username) {
        return new ResponseEntity<>(userRepository.findFirstByUsername(username),HttpStatus.OK);
    }

    @PutMapping
    public ResponseEntity<User> updateUserInfo(@RequestBody User user, @RequestHeader(value = "Authorization") String bearer) throws Exception {


        if(bearer == null || bearer.indexOf("Bearer") == -1) {
            return new ResponseEntity<>(HttpStatus.UNAUTHORIZED);
        }

        String token = bearer.replace("Bearer", "");


        Claims claims = Jwts.parser()
                .setSigningKey(jwtConfig.getSecret().getBytes())
                .parseClaimsJws(token)
                .getBody();

        String username = claims.getSubject();

        if(!username.equals(user.getUsername())) {
            throw new InvalidUserIdException("Username is not matched");
        }


        User currentUser = userRepository.findFirstByEmail(user.getEmail());

        if(currentUser == null) {
            throw new InvalidUserIdException("No user is associated with this email");
        }

        currentUser.setEmail(user.getEmail())
                    .setGender(user.getGender())
                    .setAddress(user.getAddress())
                    .setLastName(user.getLastName())
                    .setPhoneNumber(user.getPhoneNumber())
                    .setFirstName(user.getFirstName());


        User response = userRepository.save(currentUser);

        return new ResponseEntity<>(response, HttpStatus.OK);

    }

    @GetMapping(value="/users")
    public ResponseEntity<List<User>> getUsers() {
        return new ResponseEntity<>(userRepository.findAll(),HttpStatus.OK);
    }


    @PostMapping(value = "${jwt.get.token.uri}")
    public ResponseEntity<?> createAuthenticationToken(@RequestBody User authenticationRequest, HttpServletResponse response)
            throws AuthenticationException {

        logger.info("Starting to debug user "+authenticationRequest.getUsername());
        authenticate(authenticationRequest.getUsername(), authenticationRequest.getPassword());


        final UserDetails userDetails = userDetailsService
                .loadUserByUsername(authenticationRequest.getUsername());
        final String token = jwtTokenUtil.generateToken(userDetails);
        return new ResponseEntity<>(token, HttpStatus.OK);

    }

    private void authenticate(String username, String password) {
        Objects.requireNonNull(username);
        Objects.requireNonNull(password);

        try {
            authenticationManager.authenticate(new UsernamePasswordAuthenticationToken(username, password));
        } catch (DisabledException e) {
            logger.info("USER_DISABLED");
            throw new AuthenticationException("USER_DISABLED", e);
        } catch (BadCredentialsException e) {
            logger.info("INVALID_CREDENTIALS");
            throw new AuthenticationException("INVALID_CREDENTIALS", e);
        }
    }

}
