using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ai_assistant_service.Auth
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class CustomAuthorizeAttribute: AuthorizeAttribute
    {
        public const string PolicyName = "ValidUserAndSubPolicy";
        public CustomAuthorizeAttribute() 
        { 
            Policy = PolicyName;
        }       
    }
}
