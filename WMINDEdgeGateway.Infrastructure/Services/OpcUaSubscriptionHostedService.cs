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

public class OpcUaSubscriptionHostedService : BackgroundService
{
    private readonly ILogger<OpcUaSubscriptionHostedService> _logger;
    private readonly MemoryCacheService _cache;

    // Per-device sessions, subscriptions and tasks
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly ConcurrentDictionary<Guid, Task> _deviceTasks = new();
    private readonly ConcurrentDictionary<Guid, ApplicationConfiguration> _configs = new();

    public OpcUaSubscriptionHostedService(
        ILogger<OpcUaSubscriptionHostedService> logger,
        MemoryCacheService cache)
    {
        _logger = logger;
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OPC UA Subscription (PubSub) Service Started");

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

                    // ✅ FILTER: Only OPC UA PubSub devices
                    var opcuaPubSubDevices = deviceConfigs
                        .Where(d => d.protocol.Equals("opcua", StringComparison.OrdinalIgnoreCase)
                                 && d.opcuaMode?.Equals("pubsub", StringComparison.OrdinalIgnoreCase) == true)
                        .ToList();

                    if (!opcuaPubSubDevices.Any())
                    {
                        _logger.LogInformation("No OPC UA PubSub devices found in cache");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }

                    foreach (var config in opcuaPubSubDevices)
                    {
                        if (_deviceTasks.ContainsKey(config.Id)) continue;

                        // Start subscription task for this device
                        var task = Task.Run(() => SubscribeToDeviceAsync(config, stoppingToken), stoppingToken);
                        _deviceTasks.TryAdd(config.Id, task);
                    }

                    // Clean up completed tasks
                    var completed = _deviceTasks.Where(kvp => kvp.Value.IsCompleted).Select(kvp => kvp.Key).ToList();
                    foreach (var deviceId in completed)
                    {
                        _deviceTasks.TryRemove(deviceId, out _);

                        // Cleanup subscription
                        if (_subscriptions.TryRemove(deviceId, out var subscription))
                        {
                            try
                            {
                                subscription?.Delete(true);
                                subscription?.Dispose();
                            }
                            catch { }
                        }

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
                    _logger.LogError(ex, "OPC UA Subscription manager error");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        finally
        {
            // Cleanup all subscriptions and sessions
            foreach (var subscription in _subscriptions.Values)
            {
                try
                {
                    subscription?.Delete(true);
                    subscription?.Dispose();
                }
                catch { }
            }

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

    private async Task SubscribeToDeviceAsync(DeviceConfigurationDto deviceConfig, CancellationToken ct)
    {
        _logger.LogInformation("Starting OPC UA Subscription for device {DeviceId} ({DeviceName})",
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

            // Create subscription
            var subscription = CreateSubscription(deviceConfig, session);
            if (subscription == null)
            {
                _logger.LogError("Failed to create subscription for device {DeviceId}", deviceConfig.Id);
                return;
            }

            _subscriptions[deviceConfig.Id] = subscription;

            _logger.LogInformation("Subscription active for device {DeviceId}. Waiting for data changes...",
                deviceConfig.Id);

            // Keep the task alive while subscription is active
            while (!ct.IsCancellationRequested && session.Connected)
            {
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Subscription cancelled for device {DeviceId}", deviceConfig.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in subscription for device {DeviceId}", deviceConfig.Id);
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
                ApplicationName = $"WMIND Edge Gateway PubSub - {deviceConfig.deviceName}",
                ApplicationType = ApplicationType.Client,
                ApplicationUri = $"urn:WMIND:EdgeGateway:PubSub:{deviceConfig.Id}",

                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = $"pki/pubsub/{deviceConfig.Id}",
                        SubjectName = $"CN=WMIND Edge Gateway PubSub {deviceConfig.deviceName}"
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
                $"WMIND_OPC_PUBSUB_{deviceConfig.Id}",
                60000,
                null,
                null
            );

            _logger.LogInformation("Connected to OPC UA server for PubSub device {DeviceId} at {Endpoint}",
                deviceConfig.Id, deviceConfig.connectionString);

            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to OPC UA server for device {DeviceId}", deviceConfig.Id);
            return null;
        }
    }

    private Subscription? CreateSubscription(DeviceConfigurationDto deviceConfig, Session session)
    {
        try
        {
            if (deviceConfig.opcuaNodes == null || !deviceConfig.opcuaNodes.Any())
            {
                _logger.LogWarning("Device {DeviceId} has no OPC UA nodes configured", deviceConfig.Id);
                return null;
            }

            var subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingInterval = 1000,
                DisplayName = $"Subscription_{deviceConfig.deviceName}"
            };

            session.AddSubscription(subscription);
            subscription.Create();

            // Add monitored items for each node
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

                    var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                    {
                        DisplayName = node.displayName,
                        StartNodeId = nodeId,
                        SamplingInterval = 1000,
                        AttributeId = Attributes.Value
                    };

                    // Capture device and node info for notification handler
                    var deviceName = deviceConfig.deviceName;
                    var deviceId = deviceConfig.Id;
                    var unit = node.unit;

                    monitoredItem.Notification += (item, e) =>
                    {
                        OnNotification(deviceId, deviceName, item, e, unit);
                    };

                    subscription.AddItem(monitoredItem);

                    _logger.LogInformation("Added monitored item: {DisplayName} ({NodeId}) for device {DeviceId}",
                        node.displayName, node.nodeId, deviceConfig.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to add monitored item {NodeId} for device {DeviceId}",
                        node.nodeId, deviceConfig.Id);
                }
            }

            subscription.ApplyChanges();

            _logger.LogInformation("Subscription created with {Count} monitored items for device {DeviceId}",
                subscription.MonitoredItemCount, deviceConfig.Id);

            return subscription;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create subscription for device {DeviceId}", deviceConfig.Id);
            return null;
        }
    }

    private void OnNotification(
        Guid deviceId,
        string deviceName,
        MonitoredItem item,
        MonitoredItemNotificationEventArgs e,
        string? unit)
    {
        foreach (var value in item.DequeueValues())
        {
            var timestamp = DateTime.UtcNow;

            Console.WriteLine();
            Console.WriteLine(new string('=', 100));
            Console.WriteLine($"OPC UA PUBSUB DATA CHANGE | Device: {deviceName} ({deviceId})");
            Console.WriteLine($"Time: {timestamp:O}");
            Console.WriteLine(new string('-', 100));
            Console.WriteLine($"Signal: {item.DisplayName}");
            Console.WriteLine($"Value: {value.Value}");
            Console.WriteLine($"Unit: {unit ?? "N/A"}");
            Console.WriteLine($"Source Timestamp: {value.SourceTimestamp:O}");
            Console.WriteLine($"Status: {value.StatusCode}");
            Console.WriteLine(new string('=', 100));
            Console.WriteLine();

            // TODO: Write to InfluxDB if needed
        }
    }
}