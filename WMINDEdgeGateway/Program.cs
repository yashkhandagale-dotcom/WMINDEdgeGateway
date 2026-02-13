using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Application.Interfaces;
using WMINDEdgeGateway.Infrastructure.Caching;
using WMINDEdgeGateway.Infrastructure.Services;

Console.WriteLine("Edge Gateway Starting...");

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

// Read config — fail fast if anything is missing
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

// Construct HttpClients
var authHttp = new HttpClient { BaseAddress = new Uri(authBaseUrl) };
var deviceHttp = new HttpClient { BaseAddress = new Uri(deviceApiBaseUrl) };

// Build service graph
IAuthClient authClient = new AuthClient(authHttp);
IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
var tokenService = new TokenService(authClient, memoryCache, gatewayClientId, gatewayClientSecret);

IDeviceServiceClient deviceClient = new DeviceServiceClient(deviceHttp, tokenService);
var cache = new MemoryCacheService();

// *** NEW: Create InfluxDB service ***
var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var influxLogger = loggerFactory.CreateLogger<InfluxDbService>();
var influxDbService = new InfluxDbService(influxLogger, configuration);

// CancellationToken to gracefully stop polling on Ctrl+C
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nShutting down...");
    cts.Cancel();
};

try
{
    // Step 1: Acquire token
    Console.WriteLine("Getting token...");
    var token = await tokenService.GetTokenAsync();

    if (string.IsNullOrEmpty(token))
    {
        Console.WriteLine("Error: Failed to retrieve a valid token.");
        return;
    }

    Console.WriteLine("Token acquired.");

    // Step 2: Fetch device configurations
    var configs = await deviceClient.GetConfigurationsAsync(gatewayClientId);
    Console.WriteLine($"Fetched {configs.Length} configurations.");

    // Step 3: Cache ALL configurations
    var configList = configs.ToList();
    cache.Set("DeviceConfigurations", configList, TimeSpan.FromMinutes(30));
    Console.WriteLine("Configurations cached in memory.");
    cache.PrintCache();

    // Step 4: Group by protocol and dispatch
    var modbusDevices = configList.Where(c => c.Protocol == 1).ToList();
    var opcuaDevices = configList.Where(c => c.Protocol == 2).ToList();

    if (opcuaDevices.Any())
        Console.WriteLine($"Skipping {opcuaDevices.Count} OPC-UA device(s) — not implemented yet.");

    if (!modbusDevices.Any())
    {
        Console.WriteLine("No Modbus devices found. Nothing to poll.");
        return;
    }

    Console.WriteLine($"Starting Modbus polling for {modbusDevices.Count} device(s)...");

    // Cache only Modbus devices for the poller
    cache.Set("DeviceConfigurations", modbusDevices, TimeSpan.FromMinutes(30));

    // Step 5: Start Modbus poller with InfluxDB integration
    var pollerLogger = loggerFactory.CreateLogger<ModbusPollerHostedService>();

    // *** UPDATED: Pass InfluxDB service to poller ***
    var poller = new ModbusPollerHostedService(pollerLogger, configuration, cache, influxDbService);

    // Run poller until Ctrl+C
    await poller.StartAsync(cts.Token);
    Console.WriteLine("Modbus poller running with InfluxDB integration. Press Ctrl+C to stop.");

    // Keep alive until cancellation
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Gateway stopped.");
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"HTTP error: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"Unexpected error: {ex.Message}");
}
finally
{
    // *** NEW: Dispose InfluxDB service ***
    influxDbService?.Dispose();
    Console.WriteLine("InfluxDB service disposed.");
}