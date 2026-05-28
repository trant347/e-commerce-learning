using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace calendar_service.Auth
{
    /// <summary>
    /// Validates HS512-signed JWTs issued by authorization-service. The Java side uses
    /// jjwt with the raw bytes of the configured secret as the HMAC key, so we mirror
    /// that here directly instead of going through Microsoft.IdentityModel (which rejects
    /// HS512 keys shorter than 64 bytes).
    /// </summary>
    public class JwtAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly byte[] _keyBytes;
        private readonly JwtSettings _settings;

        public JwtAuthMiddleware(RequestDelegate next, IOptions<JwtSettings> settings)
        {
            _next = next;
            _settings = settings.Value;
            _keyBytes = Encoding.UTF8.GetBytes(_settings.Secret);
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var header = context.Request.Headers[_settings.Header].ToString();
            if (!string.IsNullOrEmpty(header) && header.StartsWith(_settings.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                var token = header.Substring(_settings.Prefix.Length).Trim();
                var principal = TryValidate(token);
                if (principal != null)
                {
                    context.User = principal;
                }
            }
            await _next(context);
        }

        private ClaimsPrincipal? TryValidate(string token)
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            try
            {
                var signingInput = parts[0] + "." + parts[1];
                using var hmac = new HMACSHA512(_keyBytes);
                var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
                var actual = Base64UrlDecode(parts[2]);
                if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

                var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                using var doc = JsonDocument.Parse(payloadJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("exp", out var expProp))
                {
                    var exp = expProp.GetInt64();
                    if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= exp) return null;
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
                return new ClaimsPrincipal(identity);
            }
            catch
            {
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
