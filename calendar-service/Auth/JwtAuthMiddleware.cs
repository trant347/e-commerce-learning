using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace calendar_service.Auth
{
    /// <summary>
    /// Validates HS256-signed JWTs issued by authorization-service. The Java side uses
    /// jjwt with the raw bytes of the configured secret as the HMAC key, so we mirror
    /// that here directly instead of going through Microsoft.IdentityModel.
    /// </summary>
    public class JwtAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly byte[] _keyBytes;
        private readonly JwtSettings _settings;
        private readonly ILogger<JwtAuthMiddleware> _log;

        public JwtAuthMiddleware(RequestDelegate next, IOptions<JwtSettings> settings, ILogger<JwtAuthMiddleware> log)
        {
            _next = next;
            _settings = settings.Value;
            _keyBytes = Encoding.UTF8.GetBytes(_settings.Secret);
            _log = log;
            _log.LogInformation("JwtAuthMiddleware initialised. HMAC key length = {Bytes} bytes ({Bits} bits)",
                _keyBytes.Length, _keyBytes.Length * 8);
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var header = context.Request.Headers[_settings.Header].ToString();
            if (!string.IsNullOrEmpty(header) && header.StartsWith(_settings.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                var token = header.Substring(_settings.Prefix.Length).Trim();
                var principal = TryValidate(token, context.Request.Path);
                if (principal != null)
                {
                    context.User = principal;
                }
            }
            else if (!string.IsNullOrEmpty(header))
            {
                _log.LogDebug("Authorization header present but does not start with prefix '{Prefix}' for {Path}",
                    _settings.Prefix, context.Request.Path);
            }
            await _next(context);
        }

        private ClaimsPrincipal? TryValidate(string token, PathString path)
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                _log.LogWarning("JWT rejected for {Path}: malformed token (expected 3 segments, got {Count})", path, parts.Length);
                return null;
            }

            try
            {
                // Surface the header so HS256/HS512 mismatches between issuer and verifier are obvious.
                string? alg = null;
                try
                {
                    using var headerDoc = JsonDocument.Parse(Base64UrlDecode(parts[0]));
                    if (headerDoc.RootElement.TryGetProperty("alg", out var algProp))
                    {
                        alg = algProp.GetString();
                    }
                }
                catch { /* header parse failure falls through to signature check */ }

                var signingInput = parts[0] + "." + parts[1];
                using var hmac = new HMACSHA256(_keyBytes);
                var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
                var actual = Base64UrlDecode(parts[2]);
                if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                {
                    _log.LogWarning(
                        "JWT signature mismatch for {Path}. Token alg='{Alg}' (verifier uses HS256). " +
                        "Expected sig length={ExpectedLen}, actual sig length={ActualLen}, key length={KeyLen} bytes. " +
                        "Common causes: alg mismatch between issuer and verifier, or different shared secret.",
                        path, alg, expected.Length, actual.Length, _keyBytes.Length);
                    return null;
                }

                var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                using var doc = JsonDocument.Parse(payloadJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("exp", out var expProp))
                {
                    var exp = expProp.GetInt64();
                    if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= exp)
                    {
                        _log.LogWarning("JWT expired for {Path} (exp={Exp}, now={Now})",
                            path, exp, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        return null;
                    }
                }

                var identity = new ClaimsIdentity("jwt");
                if (root.TryGetProperty("sub", out var subProp) && subProp.ValueKind == JsonValueKind.String)
                {
                    var sub = subProp.GetString()!;
                    identity.AddClaim(new Claim(ClaimTypes.Name, sub));
                    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
                }
                if (root.TryGetProperty("authorities", out var authProp) && authProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in authProp.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.String)
                        {
                            identity.AddClaim(new Claim(ClaimTypes.Role, a.GetString()!));
                        }
                    }
                }
                _log.LogDebug("JWT accepted for {Path} (sub='{Sub}', alg='{Alg}')",
                    path, identity.Name, alg);
                return new ClaimsPrincipal(identity);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "JWT validation threw for {Path}: {Type} - {Message}",
                    path, ex.GetType().Name, ex.Message);
                return null;
            }
        }

        private static byte[] Base64UrlDecode(string input)
        {
            string s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
