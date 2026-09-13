using Microsoft.AspNetCore.Authorization;

namespace ai_assistant_service.Auth;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CustomAuthorizeAttribute : AuthorizeAttribute
{
    public const string PolicyName = "AuthenticatedMarketplaceUser";

    public CustomAuthorizeAttribute()
    {
        Policy = PolicyName;
    }
}
