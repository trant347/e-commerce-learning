package test.validators;


import com.bookstore.authentication.exceptions.EmailNotAvailableException;
import com.bookstore.authentication.exceptions.InvalidEmailException;
import com.bookstore.authentication.exceptions.UsernameNotAvailableException;
import com.bookstore.authentication.model.User;
import com.bookstore.authentication.repository.UserRepository;
import com.bookstore.authentication.validators.UserValidator;
import com.mocks.MockConfiguration;
import org.junit.Before;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.ExpectedException;
import org.junit.runner.RunWith;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.context.TestPropertySource;
import org.springframework.test.context.junit4.SpringJUnit4ClassRunner;

@ActiveProfiles("test")
@RunWith(SpringJUnit4ClassRunner.class)
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

    @Rule
    public final ExpectedException exception = ExpectedException.none();

    @Before
    public void init() {
        userValidator = new UserValidator(userRepository);
    }

    @Test
    public void testEmailNotAvailableForFormValidator() throws Exception {

        User user = new User().setEmail("tony@gmail.com").setUsername("tony");

        exception.expect(EmailNotAvailableException.class);

        userValidator.validate(user);

    }


    @Test
    public void testEmailNotValid() throws Exception {

        User user = new User().setEmail("acde@com");
        exception.expect(InvalidEmailException.class);
        userValidator.validate(user);
    }


    @Test
    public void testUsernameNotAvailable() throws Exception {

        User user = new User().setUsername("tony").setEmail("tony1@gmail.com");
        exception.expect(UsernameNotAvailableException.class);
        userValidator.validate(user);

    }


}
