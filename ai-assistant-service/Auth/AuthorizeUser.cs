using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace ai_assistant_service.Auth
{
    public class AuthorizeUserRequirement : IAuthorizationRequirement
    {
    }

    public class AuthorizeUserHandler : AuthorizationHandler<AuthorizeUserRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AuthorizeUserRequirement requirement)
        {
            var user = context.User;
            if (user == null || !(user.Identity?.IsAuthenticated ?? false))
            {
                return Task.CompletedTask;
            }

            var subClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        ?? user.FindFirst("sub")?.Value;

            if (!string.IsNullOrWhiteSpace(subClaim))
            {
                // Everything checks out! Grant access.
                context.Succeed(requirement);
            }
            return Task.CompletedTask;
        }
    }
}
