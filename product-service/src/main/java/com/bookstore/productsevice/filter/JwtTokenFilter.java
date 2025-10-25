package com.bookstore.productsevice.filter;


import com.bookstore.productsevice.security.Secret;
import io.jsonwebtoken.Claims;
import io.jsonwebtoken.Jwts;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.cloud.client.ServiceInstance;
import org.springframework.core.env.Environment;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Component;

import javax.servlet.Filter;
import javax.servlet.FilterChain;
import javax.servlet.ServletException;
import javax.servlet.ServletRequest;
import javax.servlet.ServletResponse;
import javax.servlet.annotation.WebFilter;
import javax.servlet.http.HttpServletRequest;
import javax.servlet.http.HttpServletResponse;
import java.io.IOException;
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
                    .setSigningKey(secret.getKey().getBytes())
                    .parseClaimsJws(token)
                    .getBody();

            String username = claims.getSubject();

            if(username != null) {
                List<String> authorities = (List<String>)claims.get("authorities");
            }
        }catch (Exception ex){
            ((HttpServletResponse)servletResponse).sendError(HttpStatus.UNAUTHORIZED.value(), "UNAUTHORIZED ACCESS");
            return;
        }
        filterChain.doFilter(servletRequest,servletResponse);
    }
}
