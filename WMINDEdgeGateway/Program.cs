using Microsoft.Extensions.Caching.Memory;
using InfluxDB.Client;
using WMINDEdgeGateway.Application.Interfaces;
using WMINDEdgeGateway.Infrastructure.Caching;
using WMINDEdgeGateway.Infrastructure.Services;
using WMINDEdgeGateway.Infrastructure.Diagnostics;

Console.WriteLine("Edge Gateway Starting...");

// ── Configuration ─────────────────────────────────────────────────────────────
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

string gatewayClientId =
    configuration["Gateway:ClientId"]
    ?? throw new Exception("Missing Gateway:ClientId in appsettings.json");

string gatewayClientSecret =
    configuration["Gateway:ClientSecret"]
    ?? throw new Exception("Missing Gateway:ClientSecret in appsettings.json");

string authBaseUrl =
    configuration["Auth:BaseUrl"]
    ?? throw new Exception("Missing Auth:BaseUrl in appsettings.json");

string deviceApiBaseUrl =
    configuration["DeviceApi:BaseUrl"]
    ?? throw new Exception("Missing DeviceApi:BaseUrl in appsettings.json");

// ── HTTP Clients & Core Services ──────────────────────────────────────────────
var authHttp = new HttpClient { BaseAddress = new Uri(authBaseUrl) };
var deviceHttp = new HttpClient { BaseAddress = new Uri(deviceApiBaseUrl) };

IAuthClient authClient = new AuthClient(authHttp);
IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
var tokenService = new TokenService(authClient, memoryCache, gatewayClientId, gatewayClientSecret);
IDeviceServiceClient deviceClient = new DeviceServiceClient(deviceHttp, tokenService);
IMemoryCacheService cache = new MemoryCacheService();

// ── Logging ────────────────────────────────────────────────────────────────────
var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

// ── InfluxDB ───────────────────────────────────────────────────────────────────
var influxLogger = loggerFactory.CreateLogger<InfluxDbService>();
var influxDbService = new InfluxDbService(influxLogger, configuration);

var influxUrl = configuration["InfluxDB:Url"] ?? "http://localhost:8087";
var influxToken = configuration["InfluxDB:Token"];
var influxOrg = configuration["InfluxDB:Org"] ?? "Wonderbiz";
var influxOptions = new InfluxDBClientOptions(influxUrl) { Token = influxToken, Org = influxOrg };
var influxClient = new InfluxDBClient(influxOptions);
Console.WriteLine($"InfluxDB client initialized: {influxUrl}");

// ── Bridge Service (InfluxDB → RabbitMQ) ──────────────────────────────────────
var bridgeLogger = loggerFactory.CreateLogger<InfluxToRabbitMqBridgeService>();
var bridgeService = new InfluxToRabbitMqBridgeService(bridgeLogger, configuration, influxClient);

// ── Cancellation ───────────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nShutting down...");
    cts.Cancel();
};

// ── Resource Monitor (CPU / RAM / Disk — no RabbitMQ needed, start early) ─────
var resourceMonitor = new ResourceMonitorService(
    loggerFactory.CreateLogger<ResourceMonitorService>()
);
_ = Task.Run(() => resourceMonitor.StartAsync(cts.Token));
Console.WriteLine("Resource monitor started.");

DiagnosticsPublisherService? diagPublisher = null;

try
{
    Console.WriteLine("Getting token...");
    var token = await tokenService.GetTokenAsync();
    if (string.IsNullOrEmpty(token))
    {
        Console.WriteLine("Error: Failed to retrieve a valid token.");
        return;
    }
    Console.WriteLine("Token acquired.");

    // ── Fetch & Cache Device Configurations ────────────────────────────────────
    var configs = await deviceClient.GetConfigurationsAsync(gatewayClientId);
    Console.WriteLine($"Fetched {configs.Length} configuration(s).");

    var configList = configs.ToList();
    cache.Set("DeviceConfigurations", configList, TimeSpan.FromMinutes(30));
    Console.WriteLine("Configurations cached in memory.");
    cache.PrintCache();

    // ── Partition Devices by Protocol / Mode ───────────────────────────────────
    var modbusDevices = configList
        .Where(c => c.Protocol == 1 &&
                    string.Equals(c.ModbusMode, "TCP", StringComparison.OrdinalIgnoreCase))
        .ToList();

    var modbusRtuDevices = configList
        .Where(c => c.Protocol == 1 &&
                    string.Equals(c.ModbusMode, "RTU", StringComparison.OrdinalIgnoreCase))
        .ToList();

    var opcuaPollingDevices = configList
        .Where(c => c.Protocol == 2 &&
                    string.Equals(c.OpcUaMode, "Polling", StringComparison.OrdinalIgnoreCase))
        .ToList();

    var opcuaPubSubDevices = configList
        .Where(c => c.Protocol == 2 &&
                    string.Equals(c.OpcUaMode, "PubSub", StringComparison.OrdinalIgnoreCase))
        .ToList();

    var cameraDevices = configList
    .Where(c => c.Protocol == 3)
    .ToList();

    if (!modbusDevices.Any() && !modbusRtuDevices.Any()  && !opcuaPollingDevices.Any() && !opcuaPubSubDevices.Any() && !cameraDevices.Any())
    {
        Console.WriteLine("No devices found to poll. Exiting.");
        return;
    }

    // ── Modbus TCP ─────────────────────────────────────────────────────────────
    if (modbusDevices.Any())
    {
        Console.WriteLine($"Starting Modbus polling for {modbusDevices.Count} device(s)...");
        cache.Set("ModbusDevices", modbusDevices, TimeSpan.FromMinutes(30));
        var modbusPoller = new ModbusPollerHostedService(
            loggerFactory.CreateLogger<ModbusPollerHostedService>(), configuration, cache, influxDbService);
        _ = Task.Run(() => modbusPoller.StartAsync(cts.Token));
        Console.WriteLine("Modbus poller started -> writing to InfluxDB.");
    }

    // ── Modbus RTU ─────────────────────────────────────────────────────────────
    if (modbusRtuDevices.Any())
    {
        Console.WriteLine($"Starting Modbus RTU polling for {modbusRtuDevices.Count} device(s)...");
        cache.Set("ModbusRtuDevices", modbusRtuDevices, TimeSpan.FromMinutes(30));
        var rtuPoller = new ModbusRtuPollerHostedService(
            loggerFactory.CreateLogger<ModbusRtuPollerHostedService>(), configuration, cache, influxDbService);
        _ = Task.Run(() => rtuPoller.StartAsync(cts.Token));
        Console.WriteLine("Modbus RTU poller started -> writing to InfluxDB.");
    }

    // ── OPC-UA Polling ─────────────────────────────────────────────────────────
    if (opcuaPollingDevices.Any())
    {
        Console.WriteLine($"Starting OPC-UA Polling for {opcuaPollingDevices.Count} device(s)...");
        cache.Set("OpcUaDevices", opcuaPollingDevices, TimeSpan.FromMinutes(30));
        var opcuaPoller = new OpcUaPollerHostedService(
            loggerFactory.CreateLogger<OpcUaPollerHostedService>(), cache, influxDbService);
        _ = Task.Run(() => opcuaPoller.StartAsync(cts.Token));
        Console.WriteLine("OPC-UA poller started -> writing to InfluxDB.");
    }

    // ── OPC-UA PubSub ──────────────────────────────────────────────────────────
    if (opcuaPubSubDevices.Any())
    {
        Console.WriteLine($"Starting OPC-UA PubSub for {opcuaPubSubDevices.Count} device(s)...");
        cache.Set("OpcUaSubDevices", opcuaPubSubDevices, TimeSpan.FromMinutes(30));
        var opcuaSub = new OpcUaSubscriptionService(
            loggerFactory.CreateLogger<OpcUaSubscriptionService>(), cache, influxDbService);
        _ = Task.Run(() => opcuaSub.ExecuteAsync(cts.Token));
        Console.WriteLine("OPC-UA subscription service started -> writing to InfluxDB.");
    }

   if (cameraDevices.Any())
{
    Console.WriteLine($"Starting Camera frame capture for {cameraDevices.Count} device(s)...");
    cache.Set("CameraDevices", cameraDevices, TimeSpan.FromMinutes(30));

    // ── S3 config ─────────────────────────────────────────────────────────
    var s3Region     = configuration["AWS:Region"]     ?? "ap-south-1";
    var s3BucketName = configuration["AWS:BucketName"] ?? throw new Exception("Missing AWS:BucketName");
    var s3AccessKey  = configuration["AWS:AccessKey"]  ?? throw new Exception("Missing AWS:AccessKey");
    var s3SecretKey  = configuration["AWS:SecretKey"]  ?? throw new Exception("Missing AWS:SecretKey");
     var MinIOURL  = configuration["AWS:MinIOURL"]  ?? throw new Exception("Missing AWS:MinIOURL");

   var s3Config = new Amazon.S3.AmazonS3Config
        {
            ServiceURL = MinIOURL, // MinIO endpoint
            ForcePathStyle = true // VERY IMPORTANT for MinIO
        };

    var s3Client = new Amazon.S3.AmazonS3Client(s3AccessKey, s3SecretKey, s3Config);

    var s3UploaderLogger = loggerFactory.CreateLogger<S3UploaderService>();
    IS3UploaderService s3Uploader = new S3UploaderService(s3Client, s3UploaderLogger, s3BucketName);

    var cameraLogger  = loggerFactory.CreateLogger<CameraPollerHostedService>();
    var cameraPoller  = new CameraPollerHostedService(cameraLogger, s3Uploader, cache);

    _ = Task.Run(() => cameraPoller.StartAsync(cts.Token));
    Console.WriteLine("Camera poller started → capturing frames → uploading to S3.");
}
   
    // ── Bridge (InfluxDB → RabbitMQ) ──────────────────────────────────────────
    await bridgeService.StartAsync(cts.Token);
    Console.WriteLine("Bridge service started.");

    // ── Diagnostics Publisher ──────────────────────────────────────────────────
    // Started AFTER bridgeService.StartAsync() so bridgeService.Channel is ready
    diagPublisher = new DiagnosticsPublisherService(
        loggerFactory.CreateLogger<DiagnosticsPublisherService>(),
        bridgeService.Channel!,
        gatewayClientId
    );
    _ = Task.Run(() => diagPublisher.StartAsync(cts.Token));
    Console.WriteLine($"Diagnostics publisher started → wmind_diagnostics_{gatewayClientId}");

    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nGateway stopped by user.");
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"HTTP error: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"Unexpected error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}
finally
{
    Console.WriteLine("\nStopping services...");
    try { if (diagPublisher != null) await diagPublisher.StopAsync(CancellationToken.None); } catch { }
    try { await resourceMonitor.StopAsync(CancellationToken.None); } catch { }
    try { await bridgeService.StopAsync(CancellationToken.None); } catch { }
    bridgeService.Dispose();
    influxClient?.Dispose();
    influxDbService?.Dispose();
    Console.WriteLine("All services stopped and disposed.");
}