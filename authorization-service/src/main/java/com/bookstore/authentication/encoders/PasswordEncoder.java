package com.bookstore.authentication.encoders;

public interface PasswordEncoder {
    public String encode(String password);
    public boolean matches(String raw, String encryptedPassword);
}
