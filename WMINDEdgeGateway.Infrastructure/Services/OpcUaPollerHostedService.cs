using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Infrastructure.Caching;

namespace WMINDEdgeGateway.Infrastructure.Services;

public class OpcUaPollerHostedService : BackgroundService
{
    private readonly ILogger<OpcUaPollerHostedService> _logger;
    private readonly MemoryCacheService _cache;

    // Per-device sessions and tasks
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Task> _deviceTasks = new();
    private readonly ConcurrentDictionary<Guid, ApplicationConfiguration> _configs = new();

    public OpcUaPollerHostedService(
        ILogger<OpcUaPollerHostedService> logger,
        MemoryCacheService cache)
    {
        _logger = logger;
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OPC UA Polling Service Started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deviceConfigs = _cache.Get<List<DeviceConfigurationDto>>("DeviceConfigurations");

                    if (deviceConfigs == null || !deviceConfigs.Any())
                    {
                        _logger.LogWarning("No device configurations in cache");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }

                    // ? FILTER: Only OPC UA Polling devices
                    var opcuaPollingDevices = deviceConfigs
                        .Where(d => d.protocol.Equals("opcua", StringComparison.OrdinalIgnoreCase)
                                 && d.opcuaMode?.Equals("polling", StringComparison.OrdinalIgnoreCase) == true)
                        .ToList();

                    if (!opcuaPollingDevices.Any())
                    {
                        _logger.LogInformation("No OPC UA Polling devices found in cache");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }

                    foreach (var config in opcuaPollingDevices)
                    {
                        if (_deviceTasks.ContainsKey(config.Id)) continue;

                        // Start polling loop for this device
                        var task = Task.Run(() => PollLoopForDeviceAsync(config, stoppingToken), stoppingToken);
                        _deviceTasks.TryAdd(config.Id, task);
                    }

                    // Clean up completed tasks
                    var completed = _deviceTasks.Where(kvp => kvp.Value.IsCompleted).Select(kvp => kvp.Key).ToList();
                    foreach (var deviceId in completed)
                    {
                        _deviceTasks.TryRemove(deviceId, out _);

                        // Cleanup session
                        if (_sessions.TryRemove(deviceId, out var session))
                        {
                            try
                            {
                                session?.Close();
                                session?.Dispose();
                            }
                            catch { }
                        }

                        _configs.TryRemove(deviceId, out _);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OPC UA Polling manager error");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        finally
        {
            // Cleanup all sessions
            foreach (var session in _sessions.Values)
            {
                try
                {
                    session?.Close();
                    session?.Dispose();
                }
                catch { }
            }
        }
    }

    private async Task PollLoopForDeviceAsync(DeviceConfigurationDto deviceConfig, CancellationToken ct)
    {
        _logger.LogInformation("Starting OPC UA Polling for device {DeviceId} ({DeviceName})",
            deviceConfig.Id, deviceConfig.deviceName);

        try
        {
            // Connect to OPC UA server
            var session = await ConnectToServerAsync(deviceConfig, ct);
            if (session == null)
            {
                _logger.LogError("Failed to connect to OPC UA server for device {DeviceId}", deviceConfig.Id);
                return;
            }

            _sessions[deviceConfig.Id] = session;

            // Polling loop
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PollDeviceOnceAsync(deviceConfig, session, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error polling OPC UA device {DeviceId}", deviceConfig.Id);
                }

                int delayMs = deviceConfig.pollIntervalMs > 0 ? deviceConfig.pollIntervalMs : 1000;
                await Task.Delay(delayMs, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Polling cancelled for device {DeviceId}", deviceConfig.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in polling loop for device {DeviceId}", deviceConfig.Id);
        }
    }

    private async Task<Session?> ConnectToServerAsync(DeviceConfigurationDto deviceConfig, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(deviceConfig.connectionString))
            {
                _logger.LogError("Device {DeviceId} has no connectionString", deviceConfig.Id);
                return null;
            }

            // Create application configuration
            var config = new ApplicationConfiguration
            {
                ApplicationName = $"WMIND Edge Gateway - {deviceConfig.deviceName}",
                ApplicationType = ApplicationType.Client,
                ApplicationUri = $"urn:WMIND:EdgeGateway:Client:{deviceConfig.Id}",

                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = $"pki/own/{deviceConfig.Id}",
                        SubjectName = $"CN=WMIND Edge Gateway Client {deviceConfig.deviceName}"
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

            // Create certificate if needed
            if (config.SecurityConfiguration.ApplicationCertificate.Certificate == null)
            {
                await config.CertificateValidator.Update(config.SecurityConfiguration);

                var certificate = await config.SecurityConfiguration.ApplicationCertificate.Find(true);

                if (certificate == null)
                {
                    certificate = CertificateFactory.CreateCertificate(
                        config.SecurityConfiguration.ApplicationCertificate.StoreType,
                        config.SecurityConfiguration.ApplicationCertificate.StorePath,
                        null,
                        config.ApplicationUri,
                        config.ApplicationName,
                        config.SecurityConfiguration.ApplicationCertificate.SubjectName,
                        null,
                        2048,
                        DateTime.UtcNow.AddDays(-1),
                        120,
                        256
                    );

                    config.SecurityConfiguration.ApplicationCertificate.Certificate = certificate;
                }
            }

            _configs[deviceConfig.Id] = config;

            // Select endpoint
            var endpoint = CoreClientUtils.SelectEndpoint(
                deviceConfig.connectionString,
                false
            );

            var endpointConfig = EndpointConfiguration.Create(config);
            var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, endpointConfig);

            // Create session
            var session = await Session.Create(
                config,
                configuredEndpoint,
                false,
                $"WMIND_OPC_POLLING_{deviceConfig.Id}",
                60000,
                null,
                null
            );

            _logger.LogInformation("Connected to OPC UA server for device {DeviceId} at {Endpoint}",
                deviceConfig.Id, deviceConfig.connectionString);

            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to OPC UA server for device {DeviceId}", deviceConfig.Id);
            return null;
        }
    }

    private async Task PollDeviceOnceAsync(
        DeviceConfigurationDto deviceConfig,
        Session session,
        CancellationToken ct)
    {
        if (session == null || !session.Connected)
        {
            _logger.LogWarning("Session not connected for device {DeviceId}", deviceConfig.Id);
            return;
        }

        if (deviceConfig.opcuaNodes == null || !deviceConfig.opcuaNodes.Any())
        {
            _logger.LogWarning("Device {DeviceId} has no OPC UA nodes configured", deviceConfig.Id);
            return;
        }

        var timestamp = DateTime.UtcNow;

        Console.WriteLine();
        Console.WriteLine(new string('=', 100));
        Console.WriteLine($"OPC UA POLLING | Device: {deviceConfig.deviceName} ({deviceConfig.Id})");
        Console.WriteLine($"Endpoint: {deviceConfig.connectionString} | Time: {timestamp:O}");
        Console.WriteLine(new string('-', 100));
        Console.WriteLine($"{"Node ID".PadRight(40)} | {"Display Name".PadRight(20)} | {"Value".PadRight(20)} | {"Unit".PadRight(10)}");
        Console.WriteLine(new string('-', 100));

        foreach (var node in deviceConfig.opcuaNodes)
        {
            if (!node.isHealthy)
            {
                _logger.LogDebug("Skipping unhealthy node {NodeId} for device {DeviceId}",
                    node.nodeId, deviceConfig.Id);
                continue;
            }

            try
            {
                var nodeId = NodeId.Parse(node.nodeId);
                var value = session.ReadValue(nodeId);

                Console.WriteLine($"{node.nodeId.PadRight(40)} | {node.displayName.PadRight(20)} | {value.Value.ToString()?.PadRight(20) ?? "null".PadRight(20)} | {(node.unit ?? "").PadRight(10)}");

                // TODO: Write to InfluxDB if needed
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read node {NodeId} for device {DeviceId}",
                    node.nodeId, deviceConfig.Id);
                Console.WriteLine($"{node.nodeId.PadRight(40)} | {node.displayName.PadRight(20)} | {"ERROR".PadRight(20)} | {(node.unit ?? "").PadRight(10)}");
            }
        }

        Console.WriteLine(new string('=', 100));
        Console.WriteLine();
    }
}