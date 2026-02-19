using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using InfluxDB.Client;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Application.Interfaces;
using WMINDEdgeGateway.Infrastructure.Caching;
using WMINDEdgeGateway.Infrastructure.Services;

Console.WriteLine("Edge Gateway Starting... Fetching from InfluxDB and publishing to queue.");

// ── Configuration ────────────────────────────────────────────────────────────
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
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

// ── HTTP Clients & Core Services ─────────────────────────────────────────────
var authHttp   = new HttpClient { BaseAddress = new Uri(authBaseUrl) };
var deviceHttp = new HttpClient { BaseAddress = new Uri(deviceApiBaseUrl) };

IAuthClient authClient            = new AuthClient(authHttp);
IMemoryCache memoryCache          = new MemoryCache(new MemoryCacheOptions());
var tokenService                  = new TokenService(authClient, memoryCache, gatewayClientId, gatewayClientSecret);
IDeviceServiceClient deviceClient = new DeviceServiceClient(deviceHttp, tokenService);
var cache                         = new MemoryCacheService();

// ── Logging ───────────────────────────────────────────────────────────────────
var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

// ── InfluxDB ──────────────────────────────────────────────────────────────────
var influxLogger    = loggerFactory.CreateLogger<InfluxDbService>();
var influxDbService = new InfluxDbService(influxLogger, configuration);

var influxUrl     = configuration["InfluxDB:Url"]   ?? "http://localhost:8087";
var influxToken   = configuration["InfluxDB:Token"];
var influxOrg     = configuration["InfluxDB:Org"]   ?? "Wonderbiz";
var influxOptions = new InfluxDBClientOptions(influxUrl) { Token = influxToken, Org = influxOrg };
var influxClient  = new InfluxDBClient(influxOptions);
Console.WriteLine($"InfluxDB client initialized: {influxUrl}");

// ── Bridge Service (InfluxDB → RabbitMQ) ─────────────────────────────────────
var bridgeLogger  = loggerFactory.CreateLogger<InfluxToRabbitMqBridgeService>();
var bridgeService = new InfluxToRabbitMqBridgeService(bridgeLogger, configuration, influxClient);

// ── Cancellation ──────────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nShutting down...");
    cts.Cancel();
};

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

    // ── Fetch & Cache Device Configurations ───────────────────────────────────
    var configs = await deviceClient.GetConfigurationsAsync(gatewayClientId);
    Console.WriteLine($"Fetched {configs.Length} configuration(s).");

    var configList = configs.ToList();
    cache.Set("DeviceConfigurations", configList, TimeSpan.FromMinutes(30));
    Console.WriteLine("Configurations cached in memory.");
    cache.PrintCache();

    // ── Partition Devices by Protocol / Mode ──────────────────────────────────
    var modbusDevices = configList
        .Where(c => c.Protocol == 1)
        .ToList();

    var opcuaPollingDevices = configList
        .Where(c => c.Protocol == 2 &&
                    string.Equals(c.OpcUaMode, "Polling", StringComparison.OrdinalIgnoreCase))
        .ToList();

    var opcuaPubSubDevices = configList
        .Where(c => c.Protocol == 2 &&
                    string.Equals(c.OpcUaMode, "PubSub", StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (!modbusDevices.Any() && !opcuaPollingDevices.Any() && !opcuaPubSubDevices.Any())
    {
        Console.WriteLine("No devices found to poll. Exiting.");
        return;
    }

    // ── Modbus ────────────────────────────────────────────────────────────────
    if (modbusDevices.Any())
    {
        Console.WriteLine($"Starting Modbus polling for {modbusDevices.Count} device(s)...");
        cache.Set("ModbusDevices", modbusDevices, TimeSpan.FromMinutes(30));

        var modbusLogger = loggerFactory.CreateLogger<ModbusPollerHostedService>();
        var modbusPoller = new ModbusPollerHostedService(modbusLogger, configuration, cache, influxDbService);
        _ = Task.Run(() => modbusPoller.StartAsync(cts.Token));
        Console.WriteLine("Modbus poller started -> writing to InfluxDB.");
    }

    // ── OPC-UA Polling ────────────────────────────────────────────────────────
    if (opcuaPollingDevices.Any())
    {
        Console.WriteLine($"Starting OPC-UA Polling for {opcuaPollingDevices.Count} device(s)...");
        cache.Set("OpcUaDevices", opcuaPollingDevices, TimeSpan.FromMinutes(30));

        var opcuaLogger = loggerFactory.CreateLogger<OpcUaPollerHostedService>();
        var opcuaPoller = new OpcUaPollerHostedService(opcuaLogger, cache, influxDbService);
        _ = Task.Run(() => opcuaPoller.StartAsync(cts.Token));
        Console.WriteLine("OPC-UA poller started -> writing to InfluxDB.");
    }

    // ── OPC-UA PubSub ─────────────────────────────────────────────────────────
    if (opcuaPubSubDevices.Any())
    {
        Console.WriteLine($"Starting OPC-UA PubSub for {opcuaPubSubDevices.Count} device(s)...");
        cache.Set("OpcUaSubDevices", opcuaPubSubDevices, TimeSpan.FromMinutes(30));

        var opcuaSubLogger = loggerFactory.CreateLogger<OpcUaSubscriptionService>();
        var opcuaSub       = new OpcUaSubscriptionService(opcuaSubLogger, cache, influxDbService);
        _ = Task.Run(() => opcuaSub.StartAsync(cts.Token));
        Console.WriteLine("OPC-UA subscription service started -> writing to InfluxDB.");
    }

    // ── Bridge (InfluxDB → RabbitMQ) ──────────────────────────────────────────
    await bridgeService.StartAsync(cts.Token);
    Console.WriteLine("Bridge service started -> reading InfluxDB and pushing to RabbitMQ.");

    Console.WriteLine("\n" + new string('=', 70));
    Console.WriteLine("                    EDGE GATEWAY RUNNING                          ");
    Console.WriteLine(new string('=', 70));
    Console.WriteLine("  Data Flow: Modbus / OPC-UA -> InfluxDB -> RabbitMQ -> Cloud");
    Console.WriteLine("  Press Ctrl+C to stop.");
    Console.WriteLine(new string('=', 70) + "\n");

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
    try { await bridgeService.StopAsync(CancellationToken.None); } catch { }
    bridgeService.Dispose();
    influxClient?.Dispose();
    influxDbService?.Dispose();
    Console.WriteLine("All services stopped and disposed.");
}
