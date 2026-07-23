using calendar_service.Services;

namespace calendar_service.Middleware
{
    /// <summary>
    /// Maps durable-enqueue storage failures to 503. The command is not publishable when its
    /// single MongoDB outbox document could not be persisted.
    /// </summary>
    public class SagaOutboxExceptionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<SagaOutboxExceptionMiddleware> _logger;

        public SagaOutboxExceptionMiddleware(
            RequestDelegate next,
            ILogger<SagaOutboxExceptionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (SagaOutboxPersistenceException ex) when (!context.Response.HasStarted)
            {
                _logger.LogError(
                    ex,
                    "Payment command could not be durably enqueued; returning 503");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Payment processing is temporarily unavailable. Please try again."
                });
            }
        }
    }
}
