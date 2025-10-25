package com.bookstore.authentication.configs;

import com.bookstore.authentication.authenticators.UserAuthenticationProvider;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.core.annotation.Order;
import org.springframework.http.HttpMethod;
import org.springframework.security.authentication.AuthenticationManager;
import org.springframework.security.config.annotation.authentication.builders.AuthenticationManagerBuilder;
import org.springframework.security.config.annotation.web.builders.HttpSecurity;
import org.springframework.security.config.annotation.web.configuration.EnableWebSecurity;
import org.springframework.security.config.annotation.web.configuration.WebSecurityConfigurerAdapter;
import org.springframework.security.config.http.SessionCreationPolicy;

import javax.servlet.http.HttpServletResponse;

//must have highest order
@Configuration
@EnableWebSecurity
@Order(1)
public class TokenAuthConfig extends WebSecurityConfigurerAdapter {


    @Autowired
    private JwtConfig jwtConfig;

    @Value("${jwt.get.token.uri}")
    private String authenticationPath;

    @Autowired
    private UserAuthenticationProvider userAuthenticationProvider;


    @Value("${jwt.get.token.uri}")
    private String jwtTokenRequest;


    @Override
    protected void configure(HttpSecurity http) throws Exception {
        http.
                csrf().disable();
                // make sure we use stateless session; session won't be used to store user's state.
//                .sessionManagement().sessionCreationPolicy(SessionCreationPolicy.STATELESS)
//              .and()
                // handle an authorized attempts
//                .exceptionHandling().authenticationEntryPoint((req, rsp, e) -> rsp.sendError(HttpServletResponse.SC_UNAUTHORIZED))
//                .and()
                // Add a filter to validate the tokens with every request
               // .addFilterBefore(new TokenAuthenticationFilter(jwtConfig), UsernamePasswordAuthenticationFilter.class)
                // authorization requests config
//                .authorizeRequests()
                // allow all who are accessing "auth" service
//                .antMatchers(HttpMethod.POST, jwtTokenRequest).permitAll()
//                .antMatchers(HttpMethod.GET, "/actuator/health", "/getSecretKey").permitAll()
//                .anyRequest().authenticated();
    }



    @Override    protected void configure(AuthenticationManagerBuilder auth) throws Exception {
        auth.authenticationProvider(userAuthenticationProvider);
        auth.userDetailsService(new JwtUserDetailService());
    }

    @Bean
    @Override
    public AuthenticationManager authenticationManagerBean() throws Exception {
        return super.authenticationManagerBean();
    }
}
