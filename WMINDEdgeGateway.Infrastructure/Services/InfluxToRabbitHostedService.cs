using InfluxDB.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Net.Sockets;
using System.Reactive;
using System.Text;
using System.Text.Json;

public class InfluxToRabbitHostedService : BackgroundService
{
    private readonly ILogger<InfluxToRabbitHostedService> _log;
    private readonly IConfiguration _config;
    private readonly InfluxDBClient _influxClient;

    private IConnection? _connection;
    private IModel? _channel;

    private const string ExchangeName = "telemetry_exchange";
    private const string QueueName = "telemetry_queue";
    private const string RoutingKey = "";

    public InfluxToRabbitHostedService(
        ILogger<InfluxToRabbitHostedService> log,
        IConfiguration config,
        InfluxDBClient influxClient)
    {
        _log = log;
        _config = config;
        _influxClient = influxClient;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var rabbitConfig = _config.GetSection("RabbitMq");
        var factory = new ConnectionFactory()
        {
            HostName = "localhost",
            Port = 5672,
            UserName = "guest",
            Password = "guest"
        };
        
        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        // Declare exchange and queue
        _channel.ExchangeDeclare(ExchangeName, type: "fanout", durable: true, autoDelete: false);
        _channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(QueueName, ExchangeName, RoutingKey);

        _log.LogInformation("RabbitMQ initialized. Exchange={Exchange}, Queue={Queue}, RoutingKey={RoutingKey}",
            ExchangeName, QueueName, RoutingKey);

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bucket = _config["InfluxDB:Bucket"];
        var org = _config["InfluxDB:Org"];

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var query = $"from(bucket:\"{bucket}\") |> range(start: -1m) |> filter(fn: (r) => r._measurement == \"modbus_telemetry\")";
                var tables = await _influxClient.GetQueryApi().QueryAsync(query, org, stoppingToken);

                var messages = new List<TelemetryMessage>();

                foreach (var table in tables)
                {
                    foreach (var record in table.Records)
                    {
                        messages.Add(new TelemetryMessage
                        {
                            DeviceId = Guid.Parse(record.GetValueByKey("deviceId").ToString()!),
                            DeviceSlaveId = Guid.Parse(record.GetValueByKey("deviceSlaveId").ToString()!),
                            SlaveIndex = int.Parse(record.GetValueByKey("slaveIndex").ToString()!),
                            RegisterAddress = int.Parse(record.GetValueByKey("registerAddress").ToString()!),
                            SignalType = record.GetValueByKey("dataType").ToString()!,
                            Value = Convert.ToDouble(record.GetValue()),
                            Unit = record.GetValueByKey("unit")?.ToString() ?? string.Empty,
                            Timestamp = DateTime.Now

                        });
                    }
                }

                foreach (var msg in messages)
                {
                    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));

                    var props = _channel!.CreateBasicProperties();
                    props.Persistent = true;
                    props.ContentType = "application/json";

                    _channel.BasicPublish(
                        exchange: ExchangeName,
                        routingKey: RoutingKey,
                        basicProperties: props,
                        body: body);

                    _log.LogInformation("Published message to RabbitMQ: {Message}", JsonSerializer.Serialize(msg));
                }

                if (messages.Count > 0)
                    _log.LogInformation("Published {Count} messages to RabbitMQ", messages.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error publishing telemetry to RabbitMQ");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _channel?.Close();
        _connection?.Close();
        _channel?.Dispose();
        _connection?.Dispose();
        return base.StopAsync(cancellationToken);
    }
}


