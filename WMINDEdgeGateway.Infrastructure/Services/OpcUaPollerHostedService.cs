// using Microsoft.Extensions.Hosting;
// using Microsoft.Extensions.Logging;
// using Microsoft.Extensions.Configuration;
// using Microsoft.Extensions.Caching.Memory;
// using Opc.Ua;
// using Opc.Ua.Client;
// using Opc.Ua.Configuration;
// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Threading;
// using System.Threading.Tasks;
// using WMINDEdgeGateway.Application.DTOs;

// namespace WMINDEdgeGateway.Infrastructure.Services
// {
//     public class OpcUaPollerHostedService : BackgroundService
//     {
//         private readonly ILogger<OpcUaPollerHostedService> _logger;
//         private readonly IConfiguration _config;
//         private readonly IMemoryCache _cache;

//         private Session? _session;

//         public OpcUaPollerHostedService(
//             ILogger<OpcUaPollerHostedService> logger,
//             IConfiguration config,
//             IMemoryCache cache)
//         {
//             _logger = logger;
//             _config = config;
//             _cache = cache;
//         }

//         protected override async Task ExecuteAsync(CancellationToken stoppingToken)
//         {
//             _logger.LogInformation("OPC UA Poller started");
            
//             await Task.Delay(2000, stoppingToken);

//             await ConnectAsync(stoppingToken);

//             while (!stoppingToken.IsCancellationRequested)
//             {
//                 var opcDevices = GetOpcDevices();

//                 foreach (var device in opcDevices)
//                 {
//                     await PollDeviceAsync(device, stoppingToken);
//                 }

//                 await Task.Delay(500, stoppingToken);
//             }
//         }

//         // ------------------------------------------------------

//         private List<DeviceConfigurationDto> GetOpcDevices()
//         {
//             if (!_cache.TryGetValue("DeviceConfigurations", out List<DeviceConfigurationDto>? devices))
//                 return new();

//             return devices
//                 .Where(d => d.Protocol.Equals("OpcUa", StringComparison.OrdinalIgnoreCase))
//                 .ToList();
//         }

//         // ------------------------------------------------------

//         private async Task PollDeviceAsync(DeviceConfigurationDto device, CancellationToken ct)
//         {
//             if (device.Slaves == null || device.Slaves.Length == 0)
//                 return;

//             var signals = device.Slaves
//                 .Where(s => s.Signals != null)
//                 .SelectMany(s => s.Signals!)
//                 .ToList();

//             if (!signals.Any())
//                 return;

//             var nodeIds = signals
//                 .Select(s => NodeId.Parse(s.NodeId))
//                 .ToList();

//             _logger.LogInformation(
//                 "Polling OPC Device: {Device} | Signals: {Count}",
//                 device.DeviceName,
//                 nodeIds.Count);

//             await ReadValuesAsync(device, signals, nodeIds);

//             await Task.Delay(device.PollIntervalMs, ct);
//         }

//         // ------------------------------------------------------

//         private async Task ConnectAsync(CancellationToken ct)
//         {
//             string endpointUrl =
//                 _config.GetValue<string>("OpcUa:EndpointUrl")
//                 ?? "opc.tcp://localhost:4840/wmind/opcua";

//             var appConfig = new ApplicationConfiguration
//             {
//                 ApplicationName = "WMIND.OpcUaClient",
//                 ApplicationUri = $"urn:{Environment.MachineName}:WMIND:OpcUaClient",
//                 ApplicationType = ApplicationType.Client,

//                 SecurityConfiguration = new SecurityConfiguration
//                 {
//                     ApplicationCertificate = new CertificateIdentifier
//                     {
//                         StoreType = "Directory",
//                         StorePath = "CertificateStores/UA_MachineDefault",
//                         SubjectName = "CN=WMIND.OpcUaClient"
//                     },

//                     TrustedPeerCertificates = new CertificateTrustList
//                     {
//                         StoreType = "Directory",
//                         StorePath = "CertificateStores/UA_TrustedPeers"
//                     },

//                     TrustedIssuerCertificates = new CertificateTrustList
//                     {
//                         StoreType = "Directory",
//                         StorePath = "CertificateStores/UA_Issuers"
//                     },

//                     RejectedCertificateStore = new CertificateTrustList
//                     {
//                         StoreType = "Directory",
//                         StorePath = "CertificateStores/UA_Rejected"
//                     },

//                     AutoAcceptUntrustedCertificates = true,
//                     AddAppCertToTrustedStore = true
//                 },

//                 TransportQuotas = new TransportQuotas
//                 {
//                     OperationTimeout = 15000
//                 },

//                 ClientConfiguration = new ClientConfiguration
//                 {
//                     DefaultSessionTimeout = 60000
//                 }
//             };

//             // Validate + auto-create certificate
//             await appConfig.Validate(ApplicationType.Client);


//             var endpoint = CoreClientUtils.SelectEndpoint(endpointUrl, false);
//             var configuredEndpoint = new ConfiguredEndpoint(null, endpoint);

//             _session = await Session.Create(
//                 appConfig,
//                 configuredEndpoint,
//                 false,
//                 "WMIND-Session",
//                 60000,
//                 null,
//                 null,
//                 ct);

//             _logger.LogInformation("Connected to OPC UA Server: {Url}", endpointUrl);
//         }

//         // ------------------------------------------------------

//         private async Task ReadValuesAsync(
//             DeviceConfigurationDto device,
//             List<OpcSignalDto> signals,
//             List<NodeId> nodeIds)
//         {
//             if (_session == null || !_session.Connected)
//                 return;

//             var readList = new ReadValueIdCollection(
//                 nodeIds.Select(n => new ReadValueId
//                 {
//                     NodeId = n,
//                     AttributeId = Attributes.Value
//                 })
//             );

//             var response = await _session.ReadAsync(
//                 null,
//                 0,
//                 TimestampsToReturn.Source,
//                 readList,
//                 CancellationToken.None);

//             for (int i = 0; i < response.Results.Count; i++)
//             {
//                 var signal = signals[i];
//                 var value = response.Results[i].Value;

//                 _logger.LogInformation(
//                     "[OPC][{Device}] {Signal} ({NodeId}) = {Value}",
//                     device.DeviceName,
//                     signal.SignalName,
//                     signal.NodeId,
//                     value
//                 );
//             }
//         }
//     }
// }
