# TaskMaster Hub
A TaskMaster marketplace that uses micro-services architecture. Users can browse task masters (service providers) for various jobs like plumbing, cleaning, tutoring, and more.

## Prerequisites
- Java, Maven, Consul and Docker

## Getting Started
1. Build docker image for each project by running `build.bat` or `build.cmd`
2. To start everything up, in the root folder, run `docker-compose up`
3. Go to `localhost:3000` to view the website

## Services
- **product-service**: TaskMaster profiles and search
- **calendar-service**: Booking management
- **authentication-service**: User authentication
- **notification-service**: Real-time notifications
- **ai-assistant-service**: AI-powered chat assistant (Ollama)

## Authentication

This project uses **shared-secret JWT (HS256)** for authentication.

### How it works
1. The client logs in via `POST /user/login` → the **authentication-service** validates credentials and issues a signed JWT using a symmetric secret key stored in Consul config.
2. The client includes the token in subsequent requests: `Authorization: Bearer <token>`.
3. Protected services (e.g. **product-service**) validate the token **locally** using the same shared secret — no network call to the authentication-service is made per request.

### Implementation detail (`JwtTokenFilter.java`)
- Applied via `@WebFilter(urlPatterns = "/products/*")`
- Only routes matching `/products/{id}` or deeper require a valid token. The list endpoint `GET /products` (no trailing segment) is **public**.
- Image files (`*.png`, `*.jpg`, `*.svg`) are always allowed through.
- The `test` Spring profile bypasses auth entirely (used in unit tests).
- Token is verified with `Jwts.parser().setSigningKey(secret.getKey())` from the `jjwt` library.

### Trade-offs
| Pro | Con |
|-----|-----|
| Fast — pure in-process validation, no extra network hop | Shared secret must be distributed to every service |
| Simple to implement | Token cannot be revoked before expiry |

### Production alternatives
- **RS256 (asymmetric JWT)** — auth-service signs with a private key; other services verify with the public key fetched from a JWKS endpoint. Used by Auth0, Keycloak, AWS Cognito.
- **API Gateway auth** — JWT validated once at the edge (Kong, AWS API Gateway); downstream services trust forwarded identity headers.
- **Token introspection** — resource service calls auth-service's `/introspect` on every request; allows real-time revocation at the cost of added latency.
