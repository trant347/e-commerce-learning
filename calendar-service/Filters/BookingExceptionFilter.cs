using calendar_service.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace calendar_service.Filters
{
    /// <summary>
    /// Maps the booking domain's exception vocabulary onto HTTP status codes so each action can
    /// stay a straight-line call into the domain services instead of repeating the same
    /// Forbid/NotFound/Conflict ladder.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: <see cref="EscrowConfigurationException"/> (a server misconfiguration)
    /// and <see cref="SagaOutboxPersistenceException"/> are left unhandled so they surface as 500
    /// and, via <c>SagaOutboxExceptionMiddleware</c>, 503 respectively. Anything else is a genuine
    /// bug and must not be disguised as a client error.
    /// </remarks>
    public sealed class BookingExceptionFilter : IExceptionFilter
    {
        public void OnException(ExceptionContext context)
        {
            var result = Map(context.Exception);
            if (result == null) return;

            context.Result = result;
            context.ExceptionHandled = true;
        }

        /// <summary>
        /// Exposed so unit tests can assert the mapping without spinning up the MVC pipeline.
        /// Returns null when the exception should keep propagating.
        /// </summary>
        public static IActionResult? Map(Exception exception) => exception switch
        {
            EscrowConfigurationException => null,
            SagaOutboxPersistenceException => null,
            UnauthorizedAccessException => new ForbidResult(),
            KeyNotFoundException => new NotFoundResult(),
            InvalidOperationException ex => new ConflictObjectResult(new { error = ex.Message }),
            _ => null
        };
    }
}
