package com.bookstore.authentication.utils;

import com.bookstore.authentication.configs.JwtConfig;
import io.jsonwebtoken.Jwts;
import io.jsonwebtoken.security.Keys;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.security.core.GrantedAuthority;
import org.springframework.security.core.userdetails.UserDetails;
import org.springframework.stereotype.Service;

import javax.crypto.SecretKey;
import java.nio.charset.StandardCharsets;
import java.util.Date;
import java.util.stream.Collectors;

@Service
public class JwtTokenUtil {

    @Autowired
    private JwtConfig jwtConfig;

    public String generateToken(UserDetails auth) {
        Long now = System.currentTimeMillis();
        SecretKey key = Keys.hmacShaKeyFor(jwtConfig.getSecret().getBytes(StandardCharsets.UTF_8));
        return Jwts.builder()
                .subject(auth.getUsername())
                // Convert to list of strings.
                // This is important because it affects the way we get them back in the Gateway.
                .claim("authorities", auth.getAuthorities().stream()
                        .map(GrantedAuthority::getAuthority).collect(Collectors.toList()))
                .issuedAt(new Date(now))
                .expiration(new Date(now + jwtConfig.getExpiration() * 1000L))
                .signWith(key, Jwts.SIG.HS256)
                .compact();
    }
}
