using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace WMINDEdgeGateway.Infrastructure.Services;

public class OpcUaSubscriptionHostedService : BackgroundService
{
    private readonly ILogger<OpcUaSubscriptionHostedService> _logger;

    private Session? _session;
    private Subscription? _subscription;
    private ApplicationConfiguration? _config;

    public OpcUaSubscriptionHostedService(
        ILogger<OpcUaSubscriptionHostedService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OPC UA PUBSUB CLIENT STARTED");

        await InitializeConfigAsync();
        await ConnectAsync();
        CreateSubscription();

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    // ---------------- CONFIG + CERTIFICATE ----------------
    private async Task InitializeConfigAsync()
    {
        _config = new ApplicationConfiguration()
        {
            ApplicationName = "WMIND Edge Gateway",
            ApplicationUri = "urn:WMIND:EdgeGateway:Client",
            ApplicationType = ApplicationType.Client,

            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = "CertificateStores/MachineDefault",
                    SubjectName = "CN=WMIND Edge Gateway Client"
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

        await _config.Validate(ApplicationType.Client);

        var app = new ApplicationInstance
        {
            ApplicationName = _config.ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = _config
        };

        bool haveCert =
            await app.CheckApplicationInstanceCertificate(false, 2048);

        if (!haveCert)
            throw new Exception("Application certificate invalid!");
    }

    // ---------------- SESSION CONNECT ----------------
    private async Task ConnectAsync()
    {
        var endpoint = CoreClientUtils.SelectEndpoint(
            "opc.tcp://localhost:4840/Simulator",
            false);

        var configuredEndpoint =
            new ConfiguredEndpoint(null, endpoint,
                EndpointConfiguration.Create(_config!));

        _session = await Session.Create(
            _config!,
            configuredEndpoint,
            false,
            "WMIND_SUB_SESSION",
            60000,
            null,
            null);

        _logger.LogInformation("OPC UA PUBSUB CONNECTED");
    }

    // ---------------- SUBSCRIPTION ----------------
    private void CreateSubscription()
    {
        if (_session == null) return;

        _subscription = new Subscription(_session.DefaultSubscription)
        {
            PublishingInterval = 1000
        };

        _session.AddSubscription(_subscription);
        _subscription.Create();

        AddMonitoredItem("VOLTAGE");
        AddMonitoredItem("TEMP");
        AddMonitoredItem("RPM");

        _subscription.ApplyChanges();

        _logger.LogInformation("OPC UA PUBSUB SUBSCRIPTION CREATED");
    }

    private void AddMonitoredItem(string signal)
    {
        var nodeId = NodeId.Parse(
            $"ns=2;s=Plant=MUMBAI_PLANT/Line=ASSEMBLY_01/Machine=CNC_02/Signal={signal}");

        var monitoredItem = new MonitoredItem(_subscription!.DefaultItem)
        {
            DisplayName = signal,
            StartNodeId = nodeId,
            SamplingInterval = 1000
        };

        monitoredItem.Notification += OnNotification;

        _subscription.AddItem(monitoredItem);
    }

    private void OnNotification(
        MonitoredItem item,
        MonitoredItemNotificationEventArgs e)
    {
        foreach (var value in item.DequeueValues())
        {
            _logger.LogInformation(
                "PUBSUB DATA | {Signal} = {Value}",
                item.DisplayName,
                value.Value);
        }
    }
}
