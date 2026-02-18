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

var authHttp = new HttpClient { BaseAddress = new Uri(authBaseUrl) };
var deviceHttp = new HttpClient { BaseAddress = new Uri(deviceApiBaseUrl) };

IAuthClient authClient = new AuthClient(authHttp);
IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
var tokenService = new TokenService(authClient, memoryCache, gatewayClientId, gatewayClientSecret);

IDeviceServiceClient deviceClient = new DeviceServiceClient(deviceHttp, tokenService);
var cache = new MemoryCacheService();

var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var influxLogger = loggerFactory.CreateLogger<InfluxDbService>();
var influxDbService = new InfluxDbService(influxLogger, configuration);

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

    var configs = await deviceClient.GetConfigurationsAsync(gatewayClientId);
    Console.WriteLine($"Fetched {configs.Length} configurations.");

    var configList = configs.ToList();

    // MODBUS
    var modbusDevices = configList.Where(c => c.Protocol == 1).ToList();

    // OPC UA
    var opcuaPollingDevices = configList
        .Where(c => c.Protocol == 2 && 
               string.Equals(c.OpcUaMode, "Polling", StringComparison.OrdinalIgnoreCase))
        .ToList();

    // var opcuaPubSubDevices = configList
    //     .Where(c => c.Protocol == 2 && 
    //            string.Equals(c.OpcUaMode, "PubSub", StringComparison.OrdinalIgnoreCase))
    //     .ToList();

    if (!modbusDevices.Any() && !opcuaPollingDevices.Any())
    {
        Console.WriteLine("No devices found to poll.");
        return;
    }

    // MODBUS START
    if (modbusDevices.Any())
    {
        Console.WriteLine($"Starting Modbus polling for {modbusDevices.Count} device(s)...");

        cache.Set("ModbusDevices", modbusDevices, TimeSpan.FromMinutes(30));

        var modbusLogger = loggerFactory.CreateLogger<ModbusPollerHostedService>();

        var modbusPoller = new ModbusPollerHostedService(
            modbusLogger,
            configuration,
            cache,
            influxDbService);

        _ = Task.Run(() => modbusPoller.StartAsync(cts.Token));
    }

    // OPC UA POLLING START
    if (opcuaPollingDevices.Any())
    {
        Console.WriteLine($"Starting OPC UA POLLING for {opcuaPollingDevices.Count} device(s)...");

        cache.Set("OpcUaDevices", opcuaPollingDevices, TimeSpan.FromMinutes(30));

        var opcuaLogger = loggerFactory.CreateLogger<OpcUaPollerHostedService>();

        var opcuaPoller = new OpcUaPollerHostedService(opcuaLogger, cache, influxDbService);

        _ = Task.Run(() => opcuaPoller.StartAsync(cts.Token));
    }

    // OPC UA PUBSUB START
    // if (opcuaPubSubDevices.Any())
    // {
    //     Console.WriteLine($"Starting OPC UA PUBSUB for {opcuaPubSubDevices.Count} device(s)...");

    //     cache.Set("OpcUaSubDevices", opcuaPubSubDevices, TimeSpan.FromMinutes(30));

    //     var opcuaSubLogger = loggerFactory.CreateLogger<OpcUaSubscriptionService>();

    //     var opcuaSub = new OpcUaSubscriptionService(opcuaSubLogger, cache, influxDbService);

    //     _ = Task.Run(() => opcuaSub.StartAsync(cts.Token));
    // }

    Console.WriteLine("Polling services running. Press Ctrl+C to stop.");

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
    influxDbService?.Dispose();
    Console.WriteLine("InfluxDB service disposed.");
}
