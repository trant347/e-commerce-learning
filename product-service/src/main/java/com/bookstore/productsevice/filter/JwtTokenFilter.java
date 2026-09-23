package com.bookstore.productsevice.filter;


import com.bookstore.productsevice.security.Secret;
import io.jsonwebtoken.Claims;
import io.jsonwebtoken.Jwts;
import javax.crypto.spec.SecretKeySpec;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
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

    private static final Logger log = LoggerFactory.getLogger(JwtTokenFilter.class);

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
        if(url.endsWith("/products/categories")
                || url.endsWith("/products/categories/metadata")) {
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

        HttpServletRequest request = (HttpServletRequest) servletRequest;
        HttpServletResponse response = (HttpServletResponse) servletResponse;
        String header = request.getHeader("Authorization");

        if(!shouldRequestAuthenticated(request.getRequestURI())) {
            filterChain.doFilter(servletRequest,servletResponse);
            return;
        }

        if(header == null || !header.startsWith("Bearer")){
            sendUnauthorized(request, response);
            return;
        }

        String token = header.replace("Bearer", "").trim();

        try {
            Claims claims = Jwts.parser()
                    .verifyWith(new SecretKeySpec(secret.getKey().getBytes(StandardCharsets.UTF_8), "HmacSHA256"))
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
            log.warn("[JwtTokenFilter] JWT verification failed for {}: {} - {}",
                    request.getRequestURI(), ex.getClass().getSimpleName(), ex.getMessage());
            sendUnauthorized(request, response);
            return;
        }
        filterChain.doFilter(servletRequest,servletResponse);
    }

    private void sendUnauthorized(HttpServletRequest request,
                                  HttpServletResponse response) throws IOException {
        if (request.getRequestURI().startsWith("/products/admin/categories")) {
            response.setStatus(HttpStatus.UNAUTHORIZED.value());
            response.setContentType("application/json");
            response.setCharacterEncoding(StandardCharsets.UTF_8.name());
            response.getWriter().write(
                    "{\"error\":\"unauthorized\",\"message\":\"Authentication is required.\"}");
            return;
        }
        response.sendError(HttpStatus.UNAUTHORIZED.value(), "UNAUTHORIZED ACCESS");
    }
}
