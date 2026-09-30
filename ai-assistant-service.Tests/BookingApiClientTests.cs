using System.Net;
using ai_assistant_service.Services.Clients;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ai_assistant_service.Tests;

public class BookingApiClientTests
{
    [Fact]
    public async Task FailureLogs_ExcludeBookingIdAndExceptionDetails()
    {
        var logger = new RecordingLogger<BookingApiClient>();
        using var http = new HttpClient(new StubHandler((_, _) =>
            throw new HttpRequestException("sentinel-private-exception")))
        {
            BaseAddress = new Uri("http://calendar-service")
        };
        var client = new BookingApiClient(http, logger);

        var result = await client.FetchBookingContextAsync(
            "sentinel-booking-id",
            CancellationToken.None);

        Assert.Equal("Booking data unavailable.", result);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains("sentinel", StringComparison.OrdinalIgnoreCase)
                || entry.Exception?.ToString().Contains(
                    "sentinel",
                    StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = new HttpClient(new StubHandler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            BaseAddress = new Uri("http://calendar-service")
        };
        var client = new BookingApiClient(http, new RecordingLogger<BookingApiClient>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.FetchBookingContextAsync("booking-id", cancellation.Token));
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((formatter(state, exception), exception));
        }
    }
}
