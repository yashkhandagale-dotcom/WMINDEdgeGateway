using InfluxDB.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WMINDEdgeGateway.Infrastructure.Diagnostics;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class InfluxToRabbitMqBridgeService : BackgroundService
    {
        private readonly ILogger<InfluxToRabbitMqBridgeService> _log;
        private readonly IConfiguration _config;
        private readonly InfluxDBClient _influxClient;

        private IConnection? _connection;
        private IModel? _channel;

        private readonly string _bucket;
        private readonly string _org;
        private readonly string _queueName;
        private readonly int _pollIntervalSeconds;

        // ── expose channel so DiagnosticsPublisherService can reuse it ────────
        public IModel? Channel => _channel;

        private DateTime _lastProcessedTime;

        public InfluxToRabbitMqBridgeService(
            ILogger<InfluxToRabbitMqBridgeService> log,
            IConfiguration config,
            InfluxDBClient influxClient)
        {
            _log = log;
            _config = config;
            _influxClient = influxClient;

            _bucket = config["InfluxDB:Bucket"] ?? "SignalTelemetryData";
            _org = config["InfluxDB:Org"] ?? "WMIND";
            _pollIntervalSeconds = config.GetValue<int?>("RabbitMq:PollIntervalSeconds") ?? 5;

            var gatewayUser = config["RabbitMq:UserName"];
            _queueName = config["RabbitMq:QueueName"] ?? "telemetry_queue";

            _lastProcessedTime = DateTime.UtcNow.AddHours(-1);

            Console.WriteLine("=======================================================");
            Console.WriteLine($"📋 BRIDGE SERVICE CONFIGURATION:");
            Console.WriteLine($"   InfluxDB Bucket: {_bucket}");
            Console.WriteLine($"   InfluxDB Org: {_org}");
            Console.WriteLine($"   RabbitMQ Queue: {_queueName}");
            Console.WriteLine($"   Poll Interval: {_pollIntervalSeconds}s");
            Console.WriteLine($"   Start Time: {_lastProcessedTime:yyyy-MM-dd HH:mm:ss} UTC");
            Console.WriteLine("=======================================================");
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            var rabbitConfig = _config.GetSection("RabbitMq");
            var hostname = rabbitConfig["HostName"] ?? "localhost";
            var port = int.Parse(rabbitConfig["Port"] ?? "5671");
            var username = rabbitConfig["UserName"] ?? "guest";

            Console.WriteLine("\n🔌 RABBITMQ CONNECTION ATTEMPT:");
            Console.WriteLine($"   Host: {hostname}  Port: {port}  User: {username}");

            // ── TLS: enabled when port is 5671, plain when 5672 ───────────────
            var useTls = port == 5671;
            var caCertPath = _config["RabbitMq:CaCertPath"] ?? "/app/ca.crt";

            var sslOption = new SslOption
            {
                Enabled = useTls,
                ServerName = hostname,
                Version = System.Security.Authentication.SslProtocols.Tls12,
            };

            if (useTls && System.IO.File.Exists(caCertPath))
            {
                // Validate server cert against our CA
                sslOption.CertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    if (errors == System.Net.Security.SslPolicyErrors.None) return true;

                    var caCert = new System.Security.Cryptography.X509Certificates.X509Certificate2(caCertPath);
                    chain!.ChainPolicy.ExtraStore.Add(caCert);
                    chain.ChainPolicy.VerificationFlags =
                        System.Security.Cryptography.X509Certificates.X509VerificationFlags.AllowUnknownCertificateAuthority;
                    chain.ChainPolicy.RevocationMode =
                        System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                    return chain.Build(
                        (System.Security.Cryptography.X509Certificates.X509Certificate2)cert!);
                };
                Console.WriteLine($"   TLS: enabled  CA cert: {caCertPath}");
            }
            else if (useTls)
            {
                // ca.crt not found — accept any cert (dev fallback)
                sslOption.CertificateValidationCallback = (_, _, _, _) => true;
                Console.WriteLine($"   TLS: enabled  CA cert: NOT FOUND — accepting any cert");
            }
            else
            {
                Console.WriteLine($"   TLS: disabled (plain AMQP)");
            }
            // ─────────────────────────────────────────────────────────────────

            var factory = new ConnectionFactory
            {
                HostName = hostname,
                Port = port,
                UserName = rabbitConfig["UserName"],
                Password = rabbitConfig["Password"],
                VirtualHost = "/",
                AutomaticRecoveryEnabled = true,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(30),
                SocketReadTimeout = TimeSpan.FromSeconds(30),
                SocketWriteTimeout = TimeSpan.FromSeconds(30),
                Ssl = sslOption
            };

            try
            {
                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();

                _channel.QueueDeclare(
                    queue: _queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);

                // ── diagnostics: connected ─────────────────────────────────────
                var s = GatewayDiagnosticsState.Instance.RabbitMqStatus;
                s.State = "connected";
                s.LastCheckedUtc = DateTime.UtcNow;
                s.ErrorMessage = null;
                // ─────────────────────────────────────────────────────────────

                Console.WriteLine($"✅ RabbitMQ Connected!  Queue '{_queueName}' ready.");
                Console.WriteLine("=======================================================\n");
                _log.LogInformation("✅ RabbitMQ initialized. Queue={Queue}", _queueName);
            }
            catch (RabbitMQ.Client.Exceptions.BrokerUnreachableException ex)
            {
                var s = GatewayDiagnosticsState.Instance.RabbitMqStatus;
                s.State = "disconnected";
                s.ErrorMessage = ex.Message;
                s.LastCheckedUtc = DateTime.UtcNow;

                Console.WriteLine($"❌ RABBITMQ FAILED: {ex.Message}");
                _log.LogError(ex, "❌ Cannot reach RabbitMQ broker at {Host}:{Port}", hostname, port);
                throw;
            }
            catch (Exception ex)
            {
                var s = GatewayDiagnosticsState.Instance.RabbitMqStatus;
                s.State = "disconnected";
                s.ErrorMessage = ex.Message;
                s.LastCheckedUtc = DateTime.UtcNow;

                Console.WriteLine($"❌ RABBITMQ FAILED: {ex.Message}");
                _log.LogError(ex, "❌ Failed to initialize RabbitMQ connection");
                throw;
            }

            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int iterationCount = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    iterationCount++;
                    var currentTime = DateTime.UtcNow;
                    var syncStart = DateTime.UtcNow;

                    var startTimeFormatted = _lastProcessedTime.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";

                    var query = $@"
                        from(bucket: ""{_bucket}"")
                          |> range(start: {startTimeFormatted})
                          |> filter(fn: (r) => r._measurement == ""modbus_telemetry"")
                          |> filter(fn: (r) => r._field == ""value"")
                          |> filter(fn: (r) => exists r.signal_id)
                    ";

                    var queryApi = _influxClient.GetQueryApi();
                    var tables = await queryApi.QueryAsync(query, _org, stoppingToken);

                    int publishedCount = 0;

                    foreach (var table in tables)
                    {
                        Console.WriteLine($"\n   Processing table with {table.Records.Count} records...");

                        foreach (var record in table.Records)
                        {
                            try
                            {
                                var signalIdStr = record.Values
                                    .FirstOrDefault(kv => kv.Key == "signal_id").Value?.ToString();

                                if (string.IsNullOrEmpty(signalIdStr))
                                {
                                    _log.LogWarning("⚠️  Record missing signal_id tag - skipping");
                                    continue;
                                }

                                var value = Convert.ToDouble(record.GetValue());
                                var timestamp = record.GetTime()?.ToDateTimeUtc() ?? DateTime.UtcNow;

                                var message = new
                                {
                                    signalId = Guid.Parse(signalIdStr),
                                    value = value,
                                    timestamp = timestamp
                                };

                                var json = JsonSerializer.Serialize(message,
                                    new JsonSerializerOptions
                                    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                                var body = Encoding.UTF8.GetBytes(json);
                                var properties = _channel!.CreateBasicProperties();
                                properties.Persistent = true;
                                properties.ContentType = "application/json";

                                _channel.BasicPublish(
                                    exchange: "",
                                    routingKey: _queueName,
                                    basicProperties: properties,
                                    body: body);

                                publishedCount++;
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "⚠️  Failed to process single record - skipping");
                            }
                        }
                    }

                    var sync = GatewayDiagnosticsState.Instance.CloudSyncStatus;
                    sync.State = "syncing";
                    sync.SyncLagSeconds = (DateTime.UtcNow - syncStart).TotalSeconds;
                    sync.LastSyncUtc = publishedCount > 0 ? DateTime.UtcNow : sync.LastSyncUtc;

                    var rmq = GatewayDiagnosticsState.Instance.RabbitMqStatus;
                    rmq.State = (_channel?.IsOpen == true) ? "connected" : "retrying";
                    rmq.LastCheckedUtc = DateTime.UtcNow;

                    _lastProcessedTime = currentTime;

                    await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Console.WriteLine("\n🛑 Cancellation requested - stopping bridge service");
                    break;
                }
                catch (Exception ex)
                {
                    GatewayDiagnosticsState.Instance.RabbitMqStatus.State = "error";
                    GatewayDiagnosticsState.Instance.RabbitMqStatus.ErrorMessage = ex.Message;
                    GatewayDiagnosticsState.Instance.CloudSyncStatus.State = "error";
                    GatewayDiagnosticsState.Instance.CloudSyncStatus.ErrorMessage = ex.Message;

                    Console.WriteLine($"\n❌ ERROR IN BRIDGE: {ex.GetType().Name}: {ex.Message}");
                    _log.LogError(ex, "❌ Error in InfluxDB to RabbitMQ bridge");

                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }

            Console.WriteLine("\n🛑 InfluxDB to RabbitMQ bridge stopped.");
            _log.LogInformation("🛑 Bridge stopped.");
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("\n🔌 Closing RabbitMQ connection...");
            try { _channel?.Close(); } catch { }
            try { _connection?.Close(); } catch { }
            try { _channel?.Dispose(); } catch { }
            try { _connection?.Dispose(); } catch { }
            Console.WriteLine("🔌 RabbitMQ connection closed.");
            _log.LogInformation("🔌 RabbitMQ connection closed.");
            return base.StopAsync(cancellationToken);
        }
    }
}