using Microsoft.AspNetCore.Authorization;

namespace ai_assistant_service.Auth;

public sealed class AuthorizeUserRequirement : IAuthorizationRequirement
{
}

public sealed class AuthorizeUserHandler : AuthorizationHandler<AuthorizeUserRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AuthorizeUserRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(claim =>
                claim.Type == "sub" && !string.IsNullOrWhiteSpace(claim.Value)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
