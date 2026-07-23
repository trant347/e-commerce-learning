using calendar_service.Middleware;
using calendar_service.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace calendar_service.Tests
{
    public class SagaOutboxExceptionMiddlewareTests
    {
        [Fact]
        public async Task InvokeAsync_PersistenceFailure_Returns503()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            var middleware = new SagaOutboxExceptionMiddleware(
                _ => throw new SagaOutboxPersistenceException(
                    "Mongo unavailable",
                    new TimeoutException()),
                NullLogger<SagaOutboxExceptionMiddleware>.Instance);

            await middleware.InvokeAsync(context);

            Assert.Equal(
                StatusCodes.Status503ServiceUnavailable,
                context.Response.StatusCode);
            context.Response.Body.Position = 0;
            var response = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Contains("temporarily unavailable", response);
        }

        [Fact]
        public async Task InvokeAsync_NonOutboxFailure_Propagates()
        {
            var context = new DefaultHttpContext();
            var middleware = new SagaOutboxExceptionMiddleware(
                _ => throw new InvalidOperationException("boom"),
                NullLogger<SagaOutboxExceptionMiddleware>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => middleware.InvokeAsync(context));
        }
    }
}
