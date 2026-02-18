using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System.Collections.Concurrent;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Infrastructure.Caching;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class OpcUaPollerHostedService : BackgroundService
    {
        private readonly ILogger<OpcUaPollerHostedService> _log;
        private readonly MemoryCacheService _cache;
        private readonly IInfluxDbService _influxDb;

        private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
        private readonly ConcurrentDictionary<Guid, Task> _deviceTasks = new();

        private static readonly object _consoleLock = new();

        private ApplicationConfiguration? _applicationConfiguration;

        public OpcUaPollerHostedService(
            ILogger<OpcUaPollerHostedService> log,
            MemoryCacheService cache,
            IInfluxDbService influxDb)
        {
            _log = log;
            _cache = cache;
            _influxDb = influxDb;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("OPC UA Poller started.");

            _applicationConfiguration = await CreateApplicationConfigurationAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deviceConfigs =
                        _cache.Get<List<DeviceConfigurationDto>>("OpcUaDevices");

                    if (deviceConfigs == null || !deviceConfigs.Any())
                    {
                        await Task.Delay(5000, stoppingToken);
                        continue;
                    }

                    foreach (var config in deviceConfigs)
                    {
                        if (config.Protocol != 2) continue;

                        if (!string.Equals(config.OpcUaMode, "Polling",
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (_deviceTasks.ContainsKey(config.Id)) continue;

                        var task = Task.Run(
                            () => PollLoopForDeviceAsync(config, stoppingToken),
                            stoppingToken);

                        _deviceTasks.TryAdd(config.Id, task);
                    }

                    CleanupCompletedDevices();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "OPC UA Poll manager error.");
                }

                await Task.Delay(5000, stoppingToken);
            }
        }

        // CERTIFICATE HANDLING
        private async Task<ApplicationConfiguration> CreateApplicationConfigurationAsync()
        {
            var config = new ApplicationConfiguration
            {
                ApplicationName = "WMIND Edge OPC UA Client",
                ApplicationType = ApplicationType.Client,
                ApplicationUri = $"urn:{Utils.GetHostName()}:WMIND:Edge",

                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = "pki/own",
                        SubjectName = "CN=WMIND Edge Gateway Client"
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
                    "CN=WMIND Edge Gateway Client",
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

        // POLLING LOOP
        private async Task PollLoopForDeviceAsync(
            DeviceConfigurationDto deviceConfig,
            CancellationToken ct)
        {
            var session = await ConnectToServerAsync(deviceConfig, ct);
            if (session == null) return;

            _sessions[deviceConfig.Id] = session;

            var uri = new Uri(deviceConfig.ConnectionString);
            var ip = uri.Host;
            var port = uri.Port;

            while (!ct.IsCancellationRequested)
            {
                int delayMs = deviceConfig.PollIntervalMs ?? 1000;

                try
                {
                    await PollSingleDeviceOnceAsync(deviceConfig, session, ip, port, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Polling error for device {Device}", deviceConfig.Id);
                }

                await Task.Delay(delayMs, ct);
            }
        }

        private async Task PollSingleDeviceOnceAsync(
            DeviceConfigurationDto deviceConfig,
            Session session,
            string ip,
            int port,
            CancellationToken ct)
        {
            if (!session.Connected) return;
            if (deviceConfig.OpcUaNodes == null ||
                !deviceConfig.OpcUaNodes.Any())
                return;

            var now = DateTime.UtcNow;
            var payloads = new List<TelemetryPayload>();

            foreach (var node in deviceConfig.OpcUaNodes)
            {
                try
                {
                    var nodeId = NodeId.Parse(node.NodeId);
                    var value = session.ReadValue(nodeId);

                    if (value?.Value == null) continue;

                    double finalValue = Convert.ToDouble(value.Value);

                    payloads.Add(new TelemetryPayload(
                        node.SignalId!.Value.ToString(),
                        finalValue,
                        now));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Failed reading node {Node} for device {Device}",
                        node.NodeId,
                        deviceConfig.DeviceName);
                }
            }

            if (!payloads.Any()) return;

            try
            {
                await _influxDb.WriteAsync(payloads, ct);

                _log.LogInformation(
                    "Pushed {Count} telemetry points to InfluxDB for device {Device}",
                    payloads.Count,
                    deviceConfig.DeviceName);

                lock (_consoleLock)
                {
                    Console.WriteLine();
                    Console.WriteLine(new string('=', 65));
                    Console.WriteLine($"Device    : {deviceConfig.DeviceName} | {ip}:{port}");
                    Console.WriteLine($"Timestamp : {now:yyyy-MM-dd HH:mm:ss} UTC");
                    Console.WriteLine($"Payloads  : {payloads.Count} → InfluxDB");
                    Console.WriteLine(new string('-', 65));
                    Console.WriteLine($"  {"SignalId",-38} {"Value",10}");
                    Console.WriteLine(new string('-', 65));

                    foreach (var p in payloads.Take(10))
                        Console.WriteLine($"  {p.SignalId,-38} {p.Value,10:G6}");

                    if (payloads.Count > 10)
                        Console.WriteLine($"  ... and {payloads.Count - 10} more");

                    Console.WriteLine(new string('=', 65));
                }
            }
            catch (Exception influxEx)
            {
                _log.LogError(influxEx,
                    "Failed to write {Count} points to InfluxDB for device {Device}",
                    payloads.Count,
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
                    $"WMIND_SESSION_{deviceConfig.Id}",
                    60000,
                    null,
                    null);

                _log.LogInformation(
                    "Connected to OPC UA server {Device}",
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
