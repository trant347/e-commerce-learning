using System.Security.Claims;

namespace ai_assistant_service.Auth;

public sealed record CurrentUser(string Username, IReadOnlyCollection<string> Roles)
{
    public bool IsInRole(string role) =>
        Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

public interface ICurrentUserAccessor
{
    CurrentUser GetRequiredUser();
}

public sealed class HttpContextCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public CurrentUser GetRequiredUser()
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("An authenticated user is required.");
        }

        var username = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("The authenticated token does not contain a subject.");
        }

        var roles = principal.FindAll("authorities")
            .Concat(principal.FindAll(ClaimTypes.Role))
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CurrentUser(username, roles);
    }
}
