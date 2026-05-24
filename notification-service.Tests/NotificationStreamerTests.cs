using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using notification_service.Services;
using Xunit;

namespace notification_service.Tests
{
    /// <summary>
    /// Regression tests for the SSE bugs:
    ///   1. Output must be framed as "data: {json}\n\n" — raw JSON bytes do not trigger
    ///      EventSource.onmessage in the browser.
    ///   2. JSON property names must be camelCase — the frontend Notification interface
    ///      reads notification.type / notification.status (camelCase).
    /// </summary>
    public class NotificationStreamerTests
    {
        [Fact]
        public async Task StreamToClient_WritesSseFormattedCamelCaseJson()
        {
            var streamer = new NotificationStreamer(NullLogger<NotificationStreamer>.Instance);
            var pipe = new Pipe();
            var cts = new CancellationTokenSource();

            // Start the consumer (will block waiting for messages)
            var streamTask = streamer.StreamToClientAsync("alice", pipe.Writer, cts.Token);

            await streamer.SendNotificationAsync("alice", new NotificationStreamedEvent
            {
                Id = "n-1",
                BookingId = "b-9",
                Type = "TASKMASTER_APPLICATION_ACCEPTED",
                Message = "Welcome aboard!",
                Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                Status = "Pending",
                ActionType = "VIEW_MY_APPLICATION",
                ActionPayload = new Dictionary<string, string> { ["foo"] = "bar" }
            });

            var raw = await ReadAvailableAsync(pipe.Reader);
            cts.Cancel();
            try { await streamTask; } catch (OperationCanceledException) { }

            // SSE wire-format envelope
            Assert.StartsWith("data: ", raw);
            Assert.EndsWith("\n\n", raw);

            // Strip envelope and parse JSON payload
            var json = raw.Substring("data: ".Length, raw.Length - "data: ".Length - "\n\n".Length);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // camelCase property names (PascalCase would be the bug)
            Assert.Equal("n-1", root.GetProperty("id").GetString());
            Assert.Equal("TASKMASTER_APPLICATION_ACCEPTED", root.GetProperty("type").GetString());
            Assert.Equal("Pending", root.GetProperty("status").GetString());
            Assert.Equal("VIEW_MY_APPLICATION", root.GetProperty("actionType").GetString());
            Assert.Equal("bar", root.GetProperty("actionPayload").GetProperty("foo").GetString());

            // No PascalCase leakage
            Assert.False(root.TryGetProperty("Type", out _));
            Assert.False(root.TryGetProperty("Status", out _));
        }

        private static async Task<string> ReadAvailableAsync(PipeReader reader)
        {
            // Wait for at least one byte then drain whatever was flushed
            var result = await reader.ReadAsync();
            var bytes = result.Buffer.ToArray();
            reader.AdvanceTo(result.Buffer.End);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
