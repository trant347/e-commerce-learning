using calendar_service.Filters;
using calendar_service.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace calendar_service.Tests
{
    /// <summary>
    /// The booking actions no longer translate domain failures themselves, so the mapping this
    /// filter performs is the single place that decides whether a domain exception becomes a
    /// 403, 404, 409 — or stays an error. See <see cref="BookingExceptionFilter"/>.
    /// </summary>
    public class BookingExceptionFilterTests
    {
        [Fact]
        public void Map_UnauthorizedAccess_BecomesForbid()
        {
            Assert.IsType<ForbidResult>(
                BookingExceptionFilter.Map(new UnauthorizedAccessException()));
        }

        [Fact]
        public void Map_KeyNotFound_BecomesNotFound()
        {
            Assert.IsType<NotFoundResult>(
                BookingExceptionFilter.Map(new KeyNotFoundException()));
        }

        [Fact]
        public void Map_InvalidOperation_BecomesConflictCarryingTheDomainMessage()
        {
            var result = Assert.IsType<ConflictObjectResult>(
                BookingExceptionFilter.Map(
                    new InvalidOperationException("Booking is PENDING")));

            var error = result.Value!.GetType().GetProperty("error")!.GetValue(result.Value);
            Assert.Equal("Booking is PENDING", error);
        }

        [Fact]
        public void Map_ActivePaymentSaga_BecomesConflict()
        {
            // ActivePaymentSagaException derives from InvalidOperationException; a duplicate
            // in-flight money movement must read as a conflict, not a server error.
            var result = Assert.IsType<ConflictObjectResult>(
                BookingExceptionFilter.Map(
                    new ActivePaymentSagaException("bk-1", "FUND_ESCROW")));

            var error = (string)result.Value!.GetType()
                .GetProperty("error")!.GetValue(result.Value)!;
            Assert.Contains("bk-1", error);
        }

        [Fact]
        public void Map_EscrowMisconfiguration_IsNotSwallowed()
        {
            // A missing custody account is an operator error. Mapping it to 409 would tell the
            // user their booking is in a bad state when the deployment is what's broken.
            Assert.Null(BookingExceptionFilter.Map(
                new EscrowConfigurationException("Escrow:CustodyUserId is required")));
        }

        [Fact]
        public void Map_OutboxPersistenceFailure_IsLeftForTheMiddlewareToTurnInto503()
        {
            Assert.Null(BookingExceptionFilter.Map(
                new SagaOutboxPersistenceException("boom", new Exception())));
        }

        [Fact]
        public void Map_UnknownException_KeepsPropagating()
        {
            Assert.Null(BookingExceptionFilter.Map(new Exception("bug")));
        }

        [Fact]
        public void OnException_MarksMappedExceptionHandled()
        {
            var context = BuildContext(new KeyNotFoundException());

            new BookingExceptionFilter().OnException(context);

            Assert.True(context.ExceptionHandled);
            Assert.IsType<NotFoundResult>(context.Result);
        }

        [Fact]
        public void OnException_LeavesUnmappedExceptionUnhandled()
        {
            var context = BuildContext(new EscrowConfigurationException("missing"));

            new BookingExceptionFilter().OnException(context);

            Assert.False(context.ExceptionHandled);
            Assert.Null(context.Result);
        }

        private static ExceptionContext BuildContext(Exception exception) =>
            new(
                new ActionContext(
                    new DefaultHttpContext(),
                    new RouteData(),
                    new ActionDescriptor()),
                new List<IFilterMetadata>())
            {
                Exception = exception
            };
    }
}
