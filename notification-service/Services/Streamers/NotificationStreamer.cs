using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace notification_service.Services
{
    public class NotificationStreamer: INotificationStreamer
    {

        private readonly ILogger<NotificationStreamer> _logger;
        private readonly ConcurrentDictionary<string, Channel<NotificationStreamedEvent>> _userChannels = new();

        private readonly SemaphoreSlim _channelLock = new(1, 1);

        public NotificationStreamer(ILogger<NotificationStreamer> logger)
        {
            _logger = logger;
        }

        public Task SendNotificationAsync(string userId, NotificationStreamedEvent message)
        {
            if (_userChannels.TryGetValue(userId, out var channel))
            {
                return channel.Writer.WriteAsync(message).AsTask();
            }
            else
            {
                _logger.LogWarning("No active channel for user {UserId}", userId);
                return Task.CompletedTask;
            }
        }

        public async Task StreamToClientAsync(string userId, PipeWriter writer, CancellationToken cancellationToken)
        {
            var channel = GetOrCreateChannel(userId);
            try
            {
                while (await channel.Reader.WaitToReadAsync(cancellationToken))
                {
                    while (channel.Reader.TryRead(out var message))
                    {
                        var json = JsonSerializer.Serialize(message, NotificationJsonOptions.Serialize);
                        var sseData = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                        await writer.WriteAsync(sseData, cancellationToken);
                        await writer.FlushAsync(cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Streaming to client {UserId} cancelled", userId);
            }
            finally
            {
                RemoveChannel(userId);
                _logger.LogInformation("Streaming to client {UserId} ended", userId);
            }
        }

        private Channel<NotificationStreamedEvent> GetOrCreateChannel(string userId)
        {
            _channelLock.Wait();
            try
            {
                if (!_userChannels.TryGetValue(userId, out var channel))
                {
                    channel = Channel.CreateUnbounded<NotificationStreamedEvent>();
                    _userChannels[userId] = channel;
                }
                return channel;
            }
            finally
            {
                _channelLock.Release();
            }
        }

        private void RemoveChannel(string userId)
        {
            _channelLock.Wait();
            try
            {
                _userChannels.TryRemove(userId, out _);
            }
            finally
            {
                _channelLock.Release();
            }
        }
    }
}