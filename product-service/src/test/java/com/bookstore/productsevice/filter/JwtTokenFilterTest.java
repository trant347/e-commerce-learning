package com.bookstore.productsevice.filter;

import com.bookstore.productsevice.security.Secret;
import com.fasterxml.jackson.databind.ObjectMapper;
import jakarta.servlet.FilterChain;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import org.junit.Before;
import org.junit.Test;
import org.springframework.core.env.Environment;
import org.springframework.test.util.ReflectionTestUtils;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.Date;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.anyInt;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

/**
 * Regression coverage for {@link JwtTokenFilter#doFilter}. The bugs these tests defend against
 * all surfaced after the jjwt 0.9 → 0.13 upgrade, where verification got much stricter:
 *   - a leading space after stripping "Bearer" triggered MalformedJwtException
 *   - Keys.hmacShaKeyFor() rejected the shared HS512 secret as too short (WeakKeyException)
 *   - exceptions in the filter were swallowed, so failures were invisible in logs
 */
public class JwtTokenFilterTest {

    // 64-byte / 512-bit secret — meets HS512 minimum so we can sign with the standard API.
    private static final String SECRET_KEY =
            "ChangeMeInProductionThisIsA64ByteJwtSecretKeyForHS512AlgorithmXX";

    private JwtTokenFilter filter;
    private Secret secret;
    private Environment environment;
    private HttpServletRequest request;
    private HttpServletResponse response;
    private FilterChain chain;

    @Before
    public void setUp() {
        filter = new JwtTokenFilter();
        secret = new Secret();
        secret.setKey(SECRET_KEY);
        environment = mock(Environment.class);
        when(environment.getActiveProfiles()).thenReturn(new String[0]);

        ReflectionTestUtils.setField(filter, "secret", secret);
        ReflectionTestUtils.setField(filter, "environment", environment);

        request = mock(HttpServletRequest.class);
        response = mock(HttpServletResponse.class);
        chain = mock(FilterChain.class);
    }

    private String signToken(String subject, List<String> authorities, String signingSecret) {
        // Built with a raw HMAC so we can sign with arbitrarily short keys (mimicking the
        // legacy auth-service on jjwt 0.9 that produced tokens with a 12-byte secret).
        try {
            Base64.Encoder b64 = Base64.getUrlEncoder().withoutPadding();
            ObjectMapper mapper = new ObjectMapper();

            Map<String, Object> header = new LinkedHashMap<>();
            header.put("alg", "HS512");
            String headerB64 = b64.encodeToString(mapper.writeValueAsBytes(header));

            Map<String, Object> claims = new LinkedHashMap<>();
            claims.put("sub", subject);
            claims.put("authorities", authorities);
            claims.put("iat", new Date().getTime() / 1000);
            claims.put("exp", (System.currentTimeMillis() + 60_000) / 1000);
            String payloadB64 = b64.encodeToString(mapper.writeValueAsBytes(claims));

            String signingInput = headerB64 + "." + payloadB64;
            Mac mac = Mac.getInstance("HmacSHA512");
            mac.init(new SecretKeySpec(signingSecret.getBytes(StandardCharsets.UTF_8), "HmacSHA512"));
            String sigB64 = b64.encodeToString(mac.doFinal(signingInput.getBytes(StandardCharsets.US_ASCII)));

            return signingInput + "." + sigB64;
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }

    @Test
    public void doFilter_validToken_passesThroughAndExposesIdentity() throws Exception {
        String token = signToken("admin", List.of("ROLE_ADMIN"), SECRET_KEY);
        when(request.getRequestURI()).thenReturn("/products/123");
        when(request.getHeader("Authorization")).thenReturn("Bearer " + token);

        filter.doFilter(request, response, chain);

        verify(chain).doFilter(request, response);
        verify(response, never()).sendError(anyInt(), anyString());
        verify(request).setAttribute("authenticatedUsername", "admin");
        verify(request).setAttribute(eq("authenticatedAuthorities"), any());
    }

    /**
     * jjwt 0.13 throws MalformedJwtException ("Compact JWT strings may not contain whitespace.")
     * if the token has a leading space. Older code did header.replace("Bearer", "") which leaves
     * exactly that space. The filter must trim before parsing.
     */
    @Test
    public void doFilter_bearerHeaderWithLeadingSpace_isAccepted() throws Exception {
        String token = signToken("admin", List.of("ROLE_ADMIN"), SECRET_KEY);
        when(request.getRequestURI()).thenReturn("/products/123");
        when(request.getHeader("Authorization")).thenReturn("Bearer  " + token); // two spaces

        filter.doFilter(request, response, chain);

        verify(chain).doFilter(request, response);
        verify(response, never()).sendError(anyInt(), anyString());
    }

    @Test
    public void doFilter_missingAuthorizationHeader_returns401() throws Exception {
        when(request.getRequestURI()).thenReturn("/products/123");
        when(request.getHeader("Authorization")).thenReturn(null);

        filter.doFilter(request, response, chain);

        verify(response).sendError(eq(401), anyString());
        verify(chain, never()).doFilter(any(), any());
    }

    @Test
    public void doFilter_nonBearerHeader_returns401() throws Exception {
        when(request.getRequestURI()).thenReturn("/products/123");
        when(request.getHeader("Authorization")).thenReturn("Basic dXNlcjpwYXNz");

        filter.doFilter(request, response, chain);

        verify(response).sendError(eq(401), anyString());
        verify(chain, never()).doFilter(any(), any());
    }

    @Test
    public void doFilter_tokenSignedWithDifferentSecret_returns401() throws Exception {
        String wrongSecret = "DifferentSecretButAlsoExactly64BytesLongSoItPassesSizeRuleX1234";
        String token = signToken("admin", List.of("ROLE_ADMIN"), wrongSecret);
        when(request.getRequestURI()).thenReturn("/products/123");
        when(request.getHeader("Authorization")).thenReturn("Bearer " + token);

        filter.doFilter(request, response, chain);

        verify(response).sendError(eq(401), anyString());
        verify(chain, never()).doFilter(any(), any());
    }

    @Test
    public void doFilter_malformedToken_returns401() throws Exception {
        when(request.getRequestURI()).thenReturn("/products/123");
        when(request.getHeader("Authorization")).thenReturn("Bearer not-a-jwt");

        filter.doFilter(request, response, chain);

        verify(response).sendError(eq(401), anyString());
        verify(chain, never()).doFilter(any(), any());
    }

    /**
     * Regression for the WeakKeyException production bug. jjwt 0.13 enforces RFC 7518 minimum
     * key sizes on the *verify* path too (HS512 ≥ 512 bits = 64 bytes). The legacy 12-byte
     * "JwtSecretKey" therefore can no longer be used end-to-end — this test pins that
     * behaviour so anyone shortening the shared secret in docker-compose / configs gets a red
     * test instead of a silent runtime 401.
     */
    @Test
    public void doFilter_shortSecret_isRejectedAsWeakKey() throws Exception {
        String shortSecret = "JwtSecretKey"; // 12 bytes, the legacy value
        secret.setKey(shortSecret);
        String token = signToken("admin", List.of("ROLE_ADMIN"), shortSecret);
        when(request.getRequestURI()).thenReturn("/products/123");
        when(request.getHeader("Authorization")).thenReturn("Bearer " + token);

        filter.doFilter(request, response, chain);

        verify(response).sendError(eq(401), anyString());
        verify(chain, never()).doFilter(any(), any());
    }

    @Test
    public void doFilter_productsCategories_bypassesAuth() throws Exception {
        when(request.getRequestURI()).thenReturn("/products/categories");

        filter.doFilter(request, response, chain);

        verify(chain).doFilter(request, response);
        verify(response, never()).sendError(anyInt(), anyString());
    }

    @Test
    public void doFilter_nonProductUrl_bypassesAuth() throws Exception {
        when(request.getRequestURI()).thenReturn("/actuator/health");

        filter.doFilter(request, response, chain);

        verify(chain).doFilter(request, response);
        verify(response, never()).sendError(anyInt(), anyString());
    }

    @Test
    public void doFilter_testProfile_skipsTokenValidationEntirely() throws Exception {
        when(environment.getActiveProfiles()).thenReturn(new String[]{"test"});

        filter.doFilter(request, response, chain);

        verify(chain).doFilter(request, response);
        verify(response, never()).sendError(anyInt(), anyString());
    }
}
