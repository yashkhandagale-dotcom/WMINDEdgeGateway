using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using System.Threading;
using System.Threading.Tasks;

public class InfluxToRabbitHostedService : BackgroundService
{
    private readonly RabbitMqProducerService _rabbitMq;
    private readonly IConfiguration _configuration;

    public InfluxToRabbitHostedService(RabbitMqProducerService rabbitMq, IConfiguration configuration)
    {
        _rabbitMq = rabbitMq;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Example: read routing key from config (optional)
        var routingKey = _configuration["RabbitMq:RoutingKey"];

        while (!stoppingToken.IsCancellationRequested)
        {
            // Example: send a test message every 5 seconds
            var message = $"Hello at {DateTime.UtcNow}";
            _rabbitMq.PublishMessage(message);

            Console.WriteLine($"Published message to '{routingKey}': {message}");

            await Task.Delay(5000, stoppingToken);
        }
    }
}