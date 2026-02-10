using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace WMINDEdgeGateway.Infrastructure.Services;

public class OpcUaPollerHostedService : BackgroundService
{
    private readonly ILogger<OpcUaPollerHostedService> _logger;
    private Session? _session;

    public OpcUaPollerHostedService(ILogger<OpcUaPollerHostedService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OPC UA CLIENT STARTED");

        await ConnectAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OPC UA polling failed");
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task ConnectAsync()
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = "WMIND Edge Gateway",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = "urn:WMIND:EdgeGateway:Client",

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

        // OPC UA Server Endpoint
        var endpoint = CoreClientUtils.SelectEndpoint(
            "opc.tcp://localhost:4840/Simulator",
            false
        );

        var endpointConfig = EndpointConfiguration.Create(config);

        var configuredEndpoint = new ConfiguredEndpoint(
            null,
            endpoint,
            endpointConfig
        );

        _session = await Session.Create(
            config,
            configuredEndpoint,
            false,
            "WMIND_OPC_UA_SESSION",
            60000,
            null,
            null
        );

        _logger.LogInformation("OPC UA CLIENT CONNECTED");
    }

    private void Poll()
    {
        if (_session == null || !_session.Connected)
            return;

        try
        {
            var counter = _session.ReadValue(new NodeId("Counter1", 2));
            var random  = _session.ReadValue(new NodeId("Random1", 2));
            var sine    = _session.ReadValue(new NodeId("Sine1", 2));

            _logger.LogInformation(
                "OPC UA POLL | Counter={Counter}, Random={Random:F2}, Sine={Sine:F2}",
                counter.Value,
                random.Value,
                sine.Value
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC UA polling failed");
        }
    }
}
