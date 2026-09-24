package test.exceptions;

import com.bookstore.authentication.exceptions.EmailNotAvailableException;
import com.bookstore.authentication.exceptions.InvalidEmailException;
import com.bookstore.authentication.exceptions.RegistrationErrorAdvice;
import com.bookstore.authentication.exceptions.UsernameNotAvailableException;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.setup.MockMvcBuilders;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RestController;

import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

public class RegistrationErrorAdviceTest {

    @RestController
    static class ThrowingController {
        @PostMapping("/register/{failure}")
        public void register(@PathVariable String failure) throws Exception {
            switch (failure) {
                case "email" -> throw new EmailNotAvailableException("mary@gmail.com");
                case "username" -> throw new UsernameNotAvailableException("Mary");
                default -> throw new InvalidEmailException("mary@");
            }
        }
    }

    private MockMvc mockMvc;

    @BeforeEach
    public void init() {
        mockMvc = MockMvcBuilders.standaloneSetup(new ThrowingController())
                .setControllerAdvice(new RegistrationErrorAdvice())
                .build();
    }

    @Test
    public void emailNotAvailableReturnsReason() throws Exception {
        mockMvc.perform(post("/register/email"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error").value("email_not_available"))
                .andExpect(jsonPath("$.message").value("This email is already registered."));
    }

    @Test
    public void usernameNotAvailableReturnsReason() throws Exception {
        mockMvc.perform(post("/register/username"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error").value("username_not_available"))
                .andExpect(jsonPath("$.message").value("This username is already taken."));
    }

    @Test
    public void invalidEmailReturnsReason() throws Exception {
        mockMvc.perform(post("/register/invalid"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error").value("invalid_email"))
                .andExpect(jsonPath("$.message").value("Email does not have a valid format."));
    }
}
