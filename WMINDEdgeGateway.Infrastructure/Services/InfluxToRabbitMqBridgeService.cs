using InfluxDB.Client;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WMINDEdgeGateway.Infrastructure.Services
{

    public class InfluxToRabbitMqBridgeService : BackgroundService
    {
        private readonly ILogger<InfluxToRabbitMqBridgeService> _log;
        private readonly IConfiguration _config;
        private readonly InfluxDBClient _influxClient;

        private IConnection? _connection;
        private RabbitMQ.Client.IModel? _channel;

        private readonly string _bucket;
        private readonly string _org;
        private readonly string _queueName;
        private readonly int _pollIntervalSeconds;

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

            _queueName = config["RabbitMq:QueueName"] ?? "telemetry_queue";

            _pollIntervalSeconds = config.GetValue<int?>("RabbitMq:PollIntervalSeconds") ?? 5;

            // Start looking back 1 hour to catch any existing data
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
            var port = int.Parse(rabbitConfig["Port"] ?? "5672");
            var username = rabbitConfig["UserName"] ?? "guest";

            Console.WriteLine("\n🔌 RABBITMQ CONNECTION ATTEMPT:");
            Console.WriteLine($"   Host: {hostname}");
            Console.WriteLine($"   Port: {port}");
            Console.WriteLine($"   Username: {username}");

            _log.LogInformation("🔌 Attempting RabbitMQ connection to {Host}:{Port} as user '{User}'",
                hostname, port, username);

            var factory = new ConnectionFactory()
            {
                HostName = hostname,
                Port = 5672,
                UserName = "guest",
                Password = "guest",
                VirtualHost = "/",
                AutomaticRecoveryEnabled = true,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(30),
                SocketReadTimeout = TimeSpan.FromSeconds(30),
                SocketWriteTimeout = TimeSpan.FromSeconds(30)
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
                    arguments: null
                );

                Console.WriteLine($"✅ RabbitMQ Connected Successfully!");
                Console.WriteLine($"✅ Queue '{_queueName}' declared and ready");
                Console.WriteLine("=======================================================\n");

                _log.LogInformation("✅ RabbitMQ initialized. Queue={Queue}", _queueName);
            }
            catch (RabbitMQ.Client.Exceptions.BrokerUnreachableException ex)
            {
                Console.WriteLine($"❌ RABBITMQ CONNECTION FAILED!");
                Console.WriteLine($"❌ Cannot reach broker at {hostname}:{port}");
                Console.WriteLine($"❌ Error: {ex.Message}");
                Console.WriteLine("=======================================================\n");

                _log.LogError(ex, "❌ Cannot reach RabbitMQ broker at {Host}:{Port}. Is RabbitMQ running?",
                    hostname, port);
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ RABBITMQ CONNECTION FAILED!");
                Console.WriteLine($"❌ Error: {ex.Message}");
                Console.WriteLine("=======================================================\n");

                _log.LogError(ex, "❌ Failed to initialize RabbitMQ connection");
                throw;
            }

            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("🚀 BRIDGE SERVICE STARTED - Polling InfluxDB...\n");
            _log.LogInformation("🚀 InfluxDB to RabbitMQ bridge started. Poll interval: {Interval}s", _pollIntervalSeconds);

            int iterationCount = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    iterationCount++;
                    var currentTime = DateTime.UtcNow;

                    Console.WriteLine($"\n╔══════════════════════════════════════════════════════════");
                    Console.WriteLine($"║ ITERATION #{iterationCount} - {currentTime:yyyy-MM-dd HH:mm:ss} UTC");
                    Console.WriteLine($"╚══════════════════════════════════════════════════════════");

                    // Format timestamp correctly for Flux (Z must be outside format string)
                    var startTimeFormatted = _lastProcessedTime.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";

                    var query = $@"
                        from(bucket: ""{_bucket}"")
                          |> range(start: {startTimeFormatted})
                          |> filter(fn: (r) => r._measurement == ""modbus_telemetry"")
                          |> filter(fn: (r) => r._field == ""value"")
                          |> filter(fn: (r) => exists r.signal_id)
                    ";

                    Console.WriteLine($"🔍 Querying InfluxDB:");
                    Console.WriteLine($"   Bucket: {_bucket}");
                    Console.WriteLine($"   Org: {_org}");
                    Console.WriteLine($"   Time Range: {_lastProcessedTime:yyyy-MM-dd HH:mm:ss} UTC to NOW");
                    Console.WriteLine($"\n📝 Query:");
                    Console.WriteLine(query);

                    _log.LogDebug("🔍 Querying InfluxDB from {Start}", startTimeFormatted);

                    var queryApi = _influxClient.GetQueryApi();
                    var tables = await queryApi.QueryAsync(query, _org, stoppingToken);

                    var totalRecords = tables.Sum(t => t.Records.Count);

                    Console.WriteLine($"\n📊 Query Results:");
                    Console.WriteLine($"   Tables returned: {tables.Count}");
                    Console.WriteLine($"   Total records: {totalRecords}");

                    _log.LogDebug("📊 Query returned {TableCount} tables with {RecordCount} total records",
                        tables.Count, totalRecords);

                    if (totalRecords == 0)
                    {
                        Console.WriteLine($"   ⚠️  NO DATA FOUND in InfluxDB for this time range!");
                        Console.WriteLine($"   💡 Check if:");
                        Console.WriteLine($"      - Data exists in bucket '{_bucket}'");
                        Console.WriteLine($"      - Measurement is 'modbus_telemetry'");
                        Console.WriteLine($"      - Records have 'signal_id' tag");
                        Console.WriteLine($"      - Data was written after {_lastProcessedTime:yyyy-MM-dd HH:mm:ss} UTC");
                    }

                    int publishedCount = 0;

                    foreach (var table in tables)
                    {
                        Console.WriteLine($"\n   Processing table with {table.Records.Count} records...");

                        foreach (var record in table.Records)
                        {
                            try
                            {
                                // Extract signal_id tag from InfluxDB record
                                var signalIdStr = record.Values.FirstOrDefault(kv => kv.Key == "signal_id").Value?.ToString();

                                if (string.IsNullOrEmpty(signalIdStr))
                                {
                                    Console.WriteLine("      ⚠️  Record missing signal_id tag - SKIPPED");
                                    _log.LogWarning("⚠️  Record missing signal_id tag - skipping");
                                    continue;
                                }

                                // Extract value and timestamp
                                var value = Convert.ToDouble(record.GetValue());
                                var timestamp = record.GetTime()?.ToDateTimeUtc() ?? DateTime.UtcNow;

                                // Create message in format expected by TelemetryBackgroundService
                                var message = new
                                {
                                    signalId = Guid.Parse(signalIdStr),
                                    value = value,
                                    timestamp = timestamp
                                };

                                // Serialize with camelCase
                                var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
                                {
                                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                                });
                                var body = Encoding.UTF8.GetBytes(json);

                                var properties = _channel!.CreateBasicProperties();
                                properties.Persistent = true;
                                properties.ContentType = "application/json";

                                _channel.BasicPublish(
                                    exchange: "",
                                    routingKey: _queueName,
                                    basicProperties: properties,
                                    body: body
                                );

                                publishedCount++;

                                Console.WriteLine($"      ✅ Published #{publishedCount}:");
                                Console.WriteLine($"         SignalId: {signalIdStr}");
                                Console.WriteLine($"         Value: {value}");
                                Console.WriteLine($"         Timestamp: {timestamp:yyyy-MM-dd HH:mm:ss}");
                                Console.WriteLine($"         JSON: {json}");

                                _log.LogTrace("📤 Published: SignalId={SignalId}, Value={Value}, Time={Time}",
                                    signalIdStr, value, timestamp);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"      ❌ Failed to process record: {ex.Message}");
                                _log.LogWarning(ex, "⚠️  Failed to process single record - skipping");
                            }
                        }
                    }

                    Console.WriteLine($"\n📊 ITERATION SUMMARY:");
                    if (publishedCount > 0)
                    {
                        Console.WriteLine($"   ✅ Successfully published {publishedCount} messages to queue '{_queueName}'");
                        _log.LogInformation("✅ Bridged {Count} telemetry points from InfluxDB → RabbitMQ", publishedCount);
                    }
                    else
                    {
                        Console.WriteLine($"   💤 No messages published (no new data found)");
                        _log.LogDebug("💤 No new data to bridge (last poll: {Time})", _lastProcessedTime);
                    }

                    // Update last processed time for next iteration
                    _lastProcessedTime = currentTime;
                    Console.WriteLine($"   ⏰ Next query will start from: {_lastProcessedTime:yyyy-MM-dd HH:mm:ss} UTC");

                    // Wait before next poll
                    Console.WriteLine($"\n⏳ Waiting {_pollIntervalSeconds} seconds until next poll...");
                    await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Console.WriteLine("\n🛑 Cancellation requested - stopping bridge service");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n❌ ERROR IN BRIDGE:");
                    Console.WriteLine($"   {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"   Stack Trace: {ex.StackTrace}");

                    _log.LogError(ex, "❌ Error in InfluxDB to RabbitMQ bridge");

                    Console.WriteLine($"   ⏳ Backing off for 10 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }

            Console.WriteLine("\n🛑 InfluxDB to RabbitMQ bridge stopped.");
            _log.LogInformation("🛑 InfluxDB to RabbitMQ bridge stopped.");
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