using RabbitMQ.Client;
using System.Text;
using Microsoft.Extensions.Configuration;

public class RabbitMqProducerService : IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly string _exchangeName;
    private readonly string _routingKey;

    public RabbitMqProducerService(IConfiguration configuration)
    {
        var hostName = configuration["RabbitMq:HostName"];
        var port = int.Parse(configuration["RabbitMq:Port"]);
        var userName = configuration["RabbitMq:UserName"];
        var password = configuration["RabbitMq:Password"];
        _exchangeName = configuration["RabbitMq:ExchangeName"];
        _routingKey = configuration["RabbitMq:RoutingKey"];

        var factory = new ConnectionFactory()
        {
            HostName = hostName,
            Port = port,
            UserName = userName,
            Password = password,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        _channel.ExchangeDeclare(exchange: _exchangeName, type: ExchangeType.Direct, durable: true);

        // declare queue automatically
        var queueName = $"queue_{_routingKey}";
        _channel.QueueDeclare(queue: queueName, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(queue: queueName, exchange: _exchangeName, routingKey: _routingKey);
    }

    public void PublishMessage(string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        _channel.BasicPublish(exchange: _exchangeName, routingKey: _routingKey, basicProperties: null, body: body);
    }

    public void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
    }
}