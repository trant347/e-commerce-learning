package com.bookstore.productsevice.filter;


import com.bookstore.productsevice.security.Secret;
import io.jsonwebtoken.Claims;
import io.jsonwebtoken.Jwts;
import io.jsonwebtoken.security.Keys;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.core.env.Environment;
import org.springframework.http.HttpStatus;

import jakarta.servlet.Filter;
import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.ServletRequest;
import jakarta.servlet.ServletResponse;
import jakarta.servlet.annotation.WebFilter;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.List;

@WebFilter(urlPatterns = "/products/*")
public class JwtTokenFilter implements Filter {


    @Autowired
    Secret secret;

    @Autowired
    Environment environment;

    public boolean shouldRequestAuthenticated(String url) {
        if(url.indexOf("/products/") == -1) {
            return false;
        }
        if(url.matches(".*\\.(png|jpg|svg)$")){
            return false;
        }
        if(url.endsWith("/products/categories")) {
            return false;
        }
        return true;
    }

    @Override
    public void doFilter(ServletRequest servletRequest, ServletResponse servletResponse, FilterChain filterChain) throws ServletException,IOException {


        for(String profile : environment.getActiveProfiles()) {
            if(profile.equals("test")) {
                filterChain.doFilter(servletRequest,servletResponse);
                return;
            }
        }

        String header = ((HttpServletRequest)servletRequest).getHeader("Authorization");

        if(!shouldRequestAuthenticated(((HttpServletRequest)servletRequest).getRequestURI())) {
            filterChain.doFilter(servletRequest,servletResponse);
            return;
        }

        if(header == null || !header.startsWith("Bearer")){
            ((HttpServletResponse)servletResponse).sendError(HttpStatus.UNAUTHORIZED.value(), "UNAUTHORIZED ACCESS");
            return;
        }

        String token = header.replace("Bearer", "");

        try {
            Claims claims = Jwts.parser()
                    .verifyWith(Keys.hmacShaKeyFor(secret.getKey().getBytes(StandardCharsets.UTF_8)))
                    .build()
                    .parseSignedClaims(token)
                    .getPayload();

            String username = claims.getSubject();

            if (username != null) {
                List<String> authorities = (List<String>) claims.get("authorities");
                // Expose parsed identity to downstream controllers via request attributes
                servletRequest.setAttribute("authenticatedUsername", username);
                servletRequest.setAttribute("authenticatedAuthorities", authorities);
            }
        }catch (Exception ex){
            ((HttpServletResponse)servletResponse).sendError(HttpStatus.UNAUTHORIZED.value(), "UNAUTHORIZED ACCESS");
            return;
        }
        filterChain.doFilter(servletRequest,servletResponse);
    }
}
