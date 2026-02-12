namespace WMINDEdgeGateway.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = default!;
    public int Port { get; set; } = 80;
    public string Username { get; set; } = default!;
    public string Password { get; set; } = default!;
    public string VirtualHost { get; set; } = "/";
    public string QueueName { get; set; } = default!;
}

// public class RabbitMqOptions
// {
//     public string Host { get; set; } = "rabbitmq.wonderbiz.org";
//     public int Port { get; set; } = 15675; // MQTT WS port
//     public string Username { get; set; } = "guest";
//     public string Password { get; set; } = "guest";
//     public string QueueName { get; set; } = "telemetry_queue"; // Topic in MQTT
//     public string VirtualHost { get; set; } = "/";
// }
