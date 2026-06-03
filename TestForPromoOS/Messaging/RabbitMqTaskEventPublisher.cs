using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace TestForPromoOS.Messaging;

public sealed class RabbitMqTaskEventPublisher : ITaskEventPublisher, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqTaskEventPublisher> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly List<Task> _pending = new();
    private readonly object _pendingLock = new();
    private IConnection? _connection;
    private volatile bool _stopping;

    public RabbitMqTaskEventPublisher(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqTaskEventPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void PublishTaskCompleted(TaskCompletedMessage message)
    {
        if (_stopping)
        {
            _logger.LogWarning(
                "Shutdown in progress, dropping task.completed for {TaskId}", message.TaskId);
            return;
        }

        var task = Task.Run(() => PublishInternalAsync(message));

        lock (_pendingLock) _pending.Add(task);

        _ = task.ContinueWith(t =>
        {
            lock (_pendingLock) _pending.Remove(t);
        }, TaskScheduler.Default);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;

        Task[] snapshot;
        lock (_pendingLock) snapshot = _pending.ToArray();

        if (snapshot.Length == 0) return;

        _logger.LogInformation(
            "Waiting up to {Grace}s for {Count} in-flight RabbitMQ publish(es) to finish...",
            ShutdownGrace.TotalSeconds, snapshot.Length);

        try
        {
            await Task.WhenAll(snapshot).WaitAsync(ShutdownGrace, cancellationToken);
            _logger.LogInformation("All in-flight publishes finished cleanly.");
        }
        catch (TimeoutException)
        {
            var stillPending = snapshot.Count(t => !t.IsCompleted);
            _logger.LogWarning(
                "Shutdown grace expired; {Count} publish(es) still in-flight, abandoning them.",
                stillPending);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Shutdown cancelled before in-flight publishes finished.");
        }
    }

    private async Task PublishInternalAsync(TaskCompletedMessage message)
    {
        try
        {
            var connection = await EnsureConnectionAsync();
            if (connection is null) return;

            await using var channel = await connection.CreateChannelAsync();

            await channel.ExchangeDeclareAsync(
                exchange: _options.Exchange,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false);

            var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

            var props = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent
            };

            await channel.BasicPublishAsync(
                exchange: _options.Exchange,
                routingKey: _options.RoutingKey,
                mandatory: false,
                basicProperties: props,
                body: body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish task.completed for {TaskId}", message.TaskId);
        }
    }

    private async Task<IConnection?> EnsureConnectionAsync()
    {
        if (_connection is { IsOpen: true }) return _connection;

        await _connectionLock.WaitAsync();
        try
        {
            if (_connection is { IsOpen: true }) return _connection;

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.User,
                Password = _options.Password
            };

            _connection = await factory.CreateConnectionAsync();
            return _connection;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to RabbitMQ at {Host}:{Port}",
                _options.Host, _options.Port);
            return null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try
            {
                if (_connection.IsOpen)
                    await _connection.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while closing RabbitMQ connection");
            }

            await _connection.DisposeAsync();
            _connection = null;
        }

        _connectionLock.Dispose();
    }
}
