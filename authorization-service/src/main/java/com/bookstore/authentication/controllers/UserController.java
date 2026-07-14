package com.bookstore.authentication.controllers;

import com.bookstore.authentication.configs.JwtConfig;
import com.bookstore.authentication.encoders.PasswordEncoder;
import com.bookstore.authentication.exceptions.AuthenticationException;
import com.bookstore.authentication.exceptions.InvalidUserIdException;
import com.bookstore.authentication.messaging.UserEventPublisher;
import com.bookstore.authentication.model.User;
import com.bookstore.authentication.repository.UserRepository;
import com.bookstore.authentication.utils.JwtTokenUtil;
import com.bookstore.authentication.validators.UserValidator;
import io.jsonwebtoken.Claims;
import io.jsonwebtoken.Jwts;
import io.jsonwebtoken.security.Keys;
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
import org.springframework.web.bind.annotation.DeleteMapping;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.PutMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RestController;

import jakarta.servlet.http.HttpServletResponse;
import javax.crypto.SecretKey;
import java.nio.charset.StandardCharsets;
import java.util.Collections;
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

    @Autowired
    private UserEventPublisher userEventPublisher;


    @PostMapping(value="/register")
    public ResponseEntity<User> createUser(@RequestBody User user) throws Exception {

        userValidator.validate(user);

        user.setEncrytedPassword(passwordEncoder.encode(user.getPassword()));
        user.setPassword(null);

        if(user.getRole() == null) {
               user.setRole("user");
        }

        User createdUser = userRepository.save(user);

        // Best-effort: publish so payment-service can create this user's wallet (see
        // UserRegisteredConsumerWorker). Not fatal if it fails — payment-service lazily
        // creates a wallet on first charge as a safety net (see
        // WalletSimulationPaymentGateway), so we don't want a Kafka outage to block registration.
        try {
            userEventPublisher.publishUserRegistered(createdUser.getUsername());
        } catch (Exception e) {
            logger.error("Failed to publish USER_REGISTERED event for '{}'", createdUser.getUsername(), e);
        }

        return new ResponseEntity<>(createdUser, HttpStatus.OK);
    }

    @GetMapping(value="/{username}")
    public ResponseEntity<User> getUserByName(@PathVariable String username) {
        return new ResponseEntity<>(userRepository.findFirstByUsername(username),HttpStatus.OK);
    }

    @PutMapping(value="/{username}")
    public ResponseEntity<User> updateUserInfo(@PathVariable String username, @RequestBody User user, @RequestHeader(value = "Authorization") String bearer) throws Exception {

        if(bearer == null || bearer.indexOf("Bearer") == -1) {
            return new ResponseEntity<>(HttpStatus.UNAUTHORIZED);
        }

        String token = bearer.replace("Bearer", "").trim();

        SecretKey putKey = Keys.hmacShaKeyFor(jwtConfig.getSecret().getBytes(StandardCharsets.UTF_8));
        Claims claims = Jwts.parser()
                .verifyWith(putKey)
                .build()
                .parseSignedClaims(token)
                .getPayload();

        String tokenUsername = claims.getSubject();

        if(!tokenUsername.equals(user.getUsername())) {
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

    @DeleteMapping(value = "/{username}")
    public ResponseEntity<?> deleteUser(@PathVariable String username,
                                       @RequestHeader(value = "Authorization", required = false) String bearer) {

        if (bearer == null || bearer.indexOf("Bearer") == -1) {
            return new ResponseEntity<>(HttpStatus.UNAUTHORIZED);
        }

        Claims claims;
        try {
            SecretKey deleteKey = Keys.hmacShaKeyFor(jwtConfig.getSecret().getBytes(StandardCharsets.UTF_8));
            claims = Jwts.parser()
                    .verifyWith(deleteKey)
                    .build()
                    .parseSignedClaims(bearer.replace("Bearer", "").trim())
                    .getPayload();
        } catch (Exception e) {
            return new ResponseEntity<>(HttpStatus.UNAUTHORIZED);
        }

        String callerUsername = claims.getSubject();
        Object authoritiesClaim = claims.get("authorities");
        List<?> authorities = authoritiesClaim instanceof List ? (List<?>) authoritiesClaim : Collections.emptyList();
        boolean isAdmin = authorities.stream().anyMatch(a -> "ROLE_ADMIN".equals(String.valueOf(a)));
        boolean isSelf = Objects.equals(callerUsername, username);

        if (!isAdmin && !isSelf) {
            return new ResponseEntity<>(Collections.singletonMap("error", "You can only delete your own profile."), HttpStatus.FORBIDDEN);
        }
        if (isAdmin && isSelf) {
            return new ResponseEntity<>(Collections.singletonMap("error", "Admins cannot delete themselves."), HttpStatus.FORBIDDEN);
        }

        User target = userRepository.findFirstByUsername(username);
        if (target == null) {
            return new ResponseEntity<>(Collections.singletonMap("error", "User not found."), HttpStatus.NOT_FOUND);
        }

        // Publish-before-delete: if the broker is unreachable we abort so we never
        // leave a deleted user with no cascade event on the wire.
        try {
            userEventPublisher.publishUserDeleted(username, callerUsername);
        } catch (Exception e) {
            logger.error("Aborting delete of '{}': failed to publish USER_DELETED event", username, e);
            return new ResponseEntity<>(
                    Collections.singletonMap("error", "Event bus unavailable; user was not deleted. Please retry."),
                    HttpStatus.SERVICE_UNAVAILABLE);
        }

        userRepository.delete(target);
        logger.info("User '{}' deleted by '{}'", username, callerUsername);
        return new ResponseEntity<>(HttpStatus.NO_CONTENT);
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
