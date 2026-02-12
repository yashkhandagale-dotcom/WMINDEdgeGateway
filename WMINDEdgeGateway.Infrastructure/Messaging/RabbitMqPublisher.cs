using RabbitMQ.Client;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using WMINDEdgeGateway.Application.Interfaces;

namespace WMINDEdgeGateway.Infrastructure.Messaging;

public sealed class RabbitMqPublisher : IMessagePublisher, IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly string _queueName;
    private readonly ILogger<RabbitMqPublisher> _logger;

    public RabbitMqPublisher(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqPublisher> logger)
    {
        var opt = options.Value;
        _queueName = opt.QueueName;
        _logger = logger;

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = opt.Host,
                Port = opt.Port,
                UserName = opt.Username,
                Password = opt.Password,
                VirtualHost = opt.VirtualHost,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            _channel.QueueDeclare(
                queue: _queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null
            );

            _logger.LogInformation("RabbitMQ connection established to {Host}:{Port}, Queue: {Queue}", 
                opt.Host, opt.Port, _queueName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize RabbitMQ connection");
            throw;
        }
    }

    public async Task PublishBatchAsync<T>(
        IEnumerable<T> batch,
        CancellationToken cancellationToken)
    {
        if (batch == null || !batch.Any())
        {
            _logger.LogWarning("Empty batch provided to PublishBatchAsync");
            return;
        }

        try
        {
            int count = 0;
            foreach (var item in batch)
            {
                try
                {
                    var json = JsonSerializer.Serialize(item);
                    var body = Encoding.UTF8.GetBytes(json);

                    var basicProperties = _channel.CreateBasicProperties();
                    basicProperties.Persistent = true; // Survive broker restart
                    basicProperties.ContentType = "application/json";

                    _channel.BasicPublish(
                        exchange: "",
                        routingKey: _queueName,
                        basicProperties: basicProperties,
                        body: body
                    );

                    count++;
                    _logger.LogDebug("Published message to {Queue}: {Message}", _queueName, json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish individual message to {Queue}", _queueName);
                    throw;
                }
            }

            _logger.LogInformation("Successfully published {Count} messages to RabbitMQ queue '{Queue}'", 
                count, _queueName);

            // Allow RabbitMQ to confirm writes
            await Task.Delay(100, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing batch to RabbitMQ");
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _channel?.Close();
            _channel?.Dispose();
            _connection?.Close();
            _connection?.Dispose();
            _logger.LogInformation("RabbitMQ connection closed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing RabbitMQ resources");
        }
    }
}