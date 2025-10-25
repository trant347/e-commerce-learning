package com.bookstore.authentication.encoders;

import org.springframework.security.crypto.bcrypt.BCryptPasswordEncoder;

public class PasswordEncoderImpl implements PasswordEncoder {

    BCryptPasswordEncoder encoder;

    public PasswordEncoderImpl() {
       encoder = new BCryptPasswordEncoder();
    }

    public PasswordEncoderImpl(BCryptPasswordEncoder encoder) {
        this.encoder = encoder;
    }

    @Override
    public String encode(String password) {
        return encoder.encode(password);
    }

    @Override
    public boolean matches(String raw, String encryptedPassword) {
        return encoder.matches(raw, encryptedPassword);
    }
}
