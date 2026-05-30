package test.validators;


import com.bookstore.authentication.exceptions.EmailNotAvailableException;
import com.bookstore.authentication.exceptions.InvalidEmailException;
import com.bookstore.authentication.exceptions.UsernameNotAvailableException;
import com.bookstore.authentication.model.User;
import com.bookstore.authentication.repository.UserRepository;
import com.bookstore.authentication.validators.UserValidator;
import com.mocks.MockConfiguration;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.context.TestPropertySource;

import static org.junit.jupiter.api.Assertions.assertThrows;

@ActiveProfiles("test")
@TestPropertySource(
        properties = {
                "spring.cloud.consul.enabled=false"
        }
)
@SpringBootTest(classes = MockConfiguration.class)
public class ValidatorTest {

    @Autowired
    UserRepository userRepository;

    UserValidator userValidator;

    @BeforeEach
    public void init() {
        userValidator = new UserValidator(userRepository);
    }

    @Test
    public void testEmailNotAvailableForFormValidator() throws Exception {

        User user = new User().setEmail("tony@gmail.com").setUsername("tony");

        assertThrows(EmailNotAvailableException.class, () -> userValidator.validate(user));
    }


    @Test
    public void testEmailNotValid() throws Exception {

        User user = new User().setEmail("acde@com");
        assertThrows(InvalidEmailException.class, () -> userValidator.validate(user));
    }


    @Test
    public void testUsernameNotAvailable() throws Exception {

        User user = new User().setUsername("tony").setEmail("tony1@gmail.com");
        assertThrows(UsernameNotAvailableException.class, () -> userValidator.validate(user));
    }


}

