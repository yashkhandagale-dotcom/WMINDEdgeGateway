using InfluxDB.Client.Api.Domain;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System.Collections.Concurrent;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Infrastructure.Caching;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class OpcUaSubscriptionService 
    {
        private readonly ILogger<OpcUaSubscriptionService> _log;
        private readonly MemoryCacheService _cache;
        private readonly IInfluxDbService _influxDb;

        private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
        private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
        private readonly ConcurrentDictionary<Guid, Task> _deviceTasks = new();

        private static readonly object _consoleLock = new();

        private ApplicationConfiguration? _applicationConfiguration;

        public OpcUaSubscriptionService(
            ILogger<OpcUaSubscriptionService> log,
            MemoryCacheService cache,
            IInfluxDbService influxDb)
        {
            _log = log;
            _cache = cache;
            _influxDb = influxDb;
        }

        public async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("OPC UA Subscription (PubSub) service started.");

            _applicationConfiguration = await CreateApplicationConfigurationAsync();

            // ✅ YAHI DAALNA HAI — cache ek baar padho, har device ke liye retry task chalao
            var deviceConfigs = _cache.Get<List<DeviceConfigurationDto>>("OpcUaSubDevices");

            if (deviceConfigs == null || !deviceConfigs.Any())
            {
                _log.LogWarning("No OPC UA PubSub devices found in cache.");
                return;
            }

            foreach (var config in deviceConfigs)
            {
                if (config.Protocol != 2) continue;
                if (!string.Equals(config.OpcUaMode, "PubSub", StringComparison.OrdinalIgnoreCase)) continue;

                var capturedConfig = config;
                var task = Task.Run(() => RunWithRetryAsync(capturedConfig, stoppingToken), stoppingToken);
                _deviceTasks.TryAdd(config.Id, task);
            }

            await Task.WhenAll(_deviceTasks.Values);
        
        }

        // retry logic
        private async Task RunWithRetryAsync(DeviceConfigurationDto deviceConfig, CancellationToken ct)
        {
            int attempt = 0;
            const int maxRetries = 5;
            const int retryDelaySeconds = 10;

            while (!ct.IsCancellationRequested)
            {
                attempt++;
                _log.LogInformation("Device {Device} — attempt {Attempt}/{Max}",
                    deviceConfig.DeviceName, attempt, maxRetries);
                try
                {
                    await SubscribeToDeviceAsync(deviceConfig, ct);

                    if (ct.IsCancellationRequested) break;

                    _log.LogWarning("Device {Device} session ended. Retrying in {Delay}s...",
                        deviceConfig.DeviceName, retryDelaySeconds);
                    attempt = 0;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Device {Device} failed on attempt {Attempt}.",
                        deviceConfig.DeviceName, attempt);
                }

                if (attempt >= maxRetries)
                {
                    _log.LogError("Device {Device} exhausted {Max} retries. Giving up.",
                        deviceConfig.DeviceName, maxRetries);
                    break;
                }

                try { await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), ct); }
                catch (OperationCanceledException) { break; }

                
            }

            CleanupCompletedDevices(); // cleanup when done
        }

        // CERTIFICATE HANDLING
        private async Task<ApplicationConfiguration> CreateApplicationConfigurationAsync()
        {
            var config = new ApplicationConfiguration
            {
                ApplicationName = "WMIND Edge OPC UA PubSub Client",
                ApplicationType = ApplicationType.Client,
                ApplicationUri = $"urn:{Utils.GetHostName()}:WMIND:Edge:PubSub",

                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = "pki/own",
                        SubjectName = "CN=WMIND Edge Gateway PubSub Client"
                    },

                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "pki/trusted"
                    },

                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "pki/rejected"
                    },

                    AutoAcceptUntrustedCertificates = true
                },

                TransportQuotas = new TransportQuotas
                {
                    OperationTimeout = 15000
                },

                ClientConfiguration = new ClientConfiguration
                {
                    DefaultSessionTimeout = 60000
                }
            };

            await config.Validate(ApplicationType.Client);

            config.CertificateValidator.CertificateValidation += (s, e) =>
            {
                e.Accept = true;
                _log.LogWarning("Auto-accepted server certificate: {Subject}",
                    e.Certificate.Subject);
            };

            var cert = await config.SecurityConfiguration
                                   .ApplicationCertificate
                                   .Find(true);

            if (cert == null)
            {
                _log.LogWarning("Application certificate not found. Creating new one...");

                cert = CertificateFactory.CreateCertificate(
                    "Directory",
                    "pki/own",
                    null,
                    config.ApplicationUri,
                    config.ApplicationName,
                    "CN=WMIND Edge Gateway PubSub Client",
                    null,
                    2048,
                    DateTime.UtcNow.AddDays(-1),
                    120,
                    256
                );

                config.SecurityConfiguration.ApplicationCertificate.Certificate = cert;
            }

            _log.LogInformation("Application certificate ready.");
            return config;
        }

        // SUBSCRIPTION LOOP
        private async Task SubscribeToDeviceAsync(
            DeviceConfigurationDto deviceConfig,
            CancellationToken ct)
        {
            var session = await ConnectToServerAsync(deviceConfig, ct);
            if (session == null)
                throw new Exception($"Failed to connect to {deviceConfig.ConnectionString}"); // ← exception throw karo

            _sessions[deviceConfig.Id] = session;

            var subscription = CreateSubscription(deviceConfig, session);
            if (subscription == null)
                throw new Exception($"Failed to create subscription for {deviceConfig.DeviceName}"); // ← exception throw karo

            _subscriptions[deviceConfig.Id] = subscription;

            _log.LogInformation(
                "Subscription active for device {Device}. Waiting for data changes...",
                deviceConfig.DeviceName);

            while (!ct.IsCancellationRequested && session.Connected)
            {
                await Task.Delay(1000, ct);
            }
        }

        private Subscription? CreateSubscription(
            DeviceConfigurationDto deviceConfig,
            Session session)
        {
            try
            {
                if (deviceConfig.OpcUaNodes == null || !deviceConfig.OpcUaNodes.Any())
                {
                    _log.LogWarning("Device {Device} has no OPC UA nodes configured.",
                        deviceConfig.DeviceName);
                    return null;
                }

                var subscription = new Subscription(session.DefaultSubscription)
                {
                    PublishingInterval = deviceConfig.PollIntervalMs ?? 1000,
                    DisplayName = $"Subscription_{deviceConfig.DeviceName}"
                };

                session.AddSubscription(subscription);
                subscription.Create();

                var uri = new Uri(deviceConfig.ConnectionString);
                var ip = uri.Host;
                var port = uri.Port;

                foreach (var node in deviceConfig.OpcUaNodes)
                {
                    try
                    {
                        var nodeId = NodeId.Parse(node.NodeId);

                        var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                        {
                            DisplayName = node.NodeId,
                            StartNodeId = nodeId,
                            SamplingInterval = deviceConfig.PollIntervalMs ?? 1000,
                            AttributeId = Attributes.Value
                        };

                        // Capture per-node context for the notification closure
                        var capturedConfig = deviceConfig;
                        var capturedNode = node;
                        var capturedIp = ip;
                        var capturedPort = port;

                        monitoredItem.Notification += (item, e) =>
                            OnNotification(capturedConfig, capturedNode, item, e,
                                capturedIp, capturedPort);

                        subscription.AddItem(monitoredItem);

                        _log.LogInformation(
                            "Added monitored item: {NodeId} for device {Device}",
                            node.NodeId, deviceConfig.DeviceName);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex,
                            "Failed to add monitored item {NodeId} for device {Device}",
                            node.NodeId, deviceConfig.DeviceName);
                    }
                }

                subscription.ApplyChanges();

                _log.LogInformation(
                    "Subscription created with {Count} monitored items for device {Device}",
                    subscription.MonitoredItemCount, deviceConfig.DeviceName);

                return subscription;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to create subscription for device {Device}",
                    deviceConfig.DeviceName);
                return null;
            }
        }

        private void OnNotification(
            DeviceConfigurationDto deviceConfig,
            OpcUaNodeDto node,
            MonitoredItem item,
            MonitoredItemNotificationEventArgs e,
            string ip,
            int port)
        {
            foreach (var value in item.DequeueValues())
            {
                if (value?.Value == null) continue;

                var now = DateTime.UtcNow;

                double finalValue;
                try
                {
                    finalValue = Convert.ToDouble(value.Value);  // always double to store in influxdb
                }
                catch
                {
                    _log.LogWarning(
                        "Could not convert value for node {NodeId} on device {Device}",
                        node.NodeId, deviceConfig.DeviceName);
                    continue;
                }
                var displayValue = FormatForDisplay(finalValue, node.DataType);
                var payload = new TelemetryPayload(
                    node.SignalId!.Value.ToString(),
                    finalValue,
                    now);

                // Fire-and-forget write — keeps notification handler non-async
                _ = WriteToInfluxAsync(deviceConfig, payload, now, ip, port, value,displayValue);
            }
        }
        // display in given format in console (for demo purposes) — InfluxDB always stores as double, but we can format it for display based on the original data type
        private static string FormatForDisplay(double value, string? dataType)
        {
            var type = (dataType ?? "float").Trim().ToLowerInvariant();

            return type switch
            {
                "int" or "integer" or "int32" => ((int)value).ToString(),
                _ => ((float)value).ToString("G6")  // float32 precision
            };
        }

        private async Task WriteToInfluxAsync(
            DeviceConfigurationDto deviceConfig,
            TelemetryPayload payload,
            DateTime now,
            string ip,
            int port,
            DataValue value,
            string displayValue)
        {
            try
            {
                await _influxDb.WriteAsync(new List<TelemetryPayload> { payload },
                    CancellationToken.None);

                _log.LogInformation(
                    "Pushed PubSub telemetry point to InfluxDB for device {Device}",
                    deviceConfig.DeviceName);

                lock (_consoleLock)
                {
                    Console.WriteLine();
                    Console.WriteLine(new string('=', 65));
                    Console.WriteLine($"Device    : {deviceConfig.DeviceName} | {ip}:{port}");
                    Console.WriteLine($"Timestamp : {now:yyyy-MM-dd HH:mm:ss} UTC");
                    Console.WriteLine($"Mode      : PubSub (subscription notification)");
                    Console.WriteLine(new string('-', 65));
                    Console.WriteLine($"  {"SignalId",-38} {"Value",10}");
                    Console.WriteLine(new string('-', 65));
                    Console.WriteLine($"  {payload.SignalId,-38} {displayValue,10:G6}");
                    Console.WriteLine($"  Source TS : {value.SourceTimestamp:O}");
                    Console.WriteLine($"  Status    : {value.StatusCode}");
                    Console.WriteLine(new string('=', 65));
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Failed to write PubSub point to InfluxDB for device {Device}",
                    deviceConfig.DeviceName);
            }
        }

        private async Task<Session?> ConnectToServerAsync(
            DeviceConfigurationDto deviceConfig,
            CancellationToken ct)
        {
            try
            {
                var endpoint = CoreClientUtils.SelectEndpoint(
                    deviceConfig.ConnectionString,
                    true);

                var endpointConfig =
                    EndpointConfiguration.Create(_applicationConfiguration);

                var configuredEndpoint =
                    new ConfiguredEndpoint(null, endpoint, endpointConfig);

                var session = await Session.Create(
                    _applicationConfiguration,
                    configuredEndpoint,
                    false,
                    $"WMIND_PUBSUB_SESSION_{deviceConfig.Id}",
                    60000,
                    null,
                    null);

                _log.LogInformation(
                    "Connected to OPC UA server {Device} for PubSub",
                    deviceConfig.DeviceName);

                return session;
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Connection failed for device {Device}",
                    deviceConfig.DeviceName);
                return null;
            }
        }

        private void CleanupCompletedDevices()
        {
            var completed = _deviceTasks
                .Where(x => x.Value.IsCompleted)
                .Select(x => x.Key)
                .ToList();

            foreach (var deviceId in completed)
            {
                _deviceTasks.TryRemove(deviceId, out _);

                if (_subscriptions.TryRemove(deviceId, out var subscription))
                {
                    try
                    {
                        subscription.Delete(true);
                        subscription.Dispose();
                    }
                    catch { }
                }

                if (_sessions.TryRemove(deviceId, out var session))
                {
                    try
                    {
                        session.Close();
                        session.Dispose();
                    }
                    catch { }
                }
            }
        }
    }
}
