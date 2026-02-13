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

Console.WriteLine("Edge Gateway Starting...Fetching from influx publishing it to queue");

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
    ?? throw new Exception("Missing Services:AuthBaseUrl in appsettings.json");

string deviceApiBaseUrl =
    configuration["DeviceApi:BaseUrl"]
    ?? throw new Exception("Missing Services:DeviceApiBaseUrl in appsettings.json");

var authHttp = new HttpClient { BaseAddress = new Uri(authBaseUrl) };
var deviceHttp = new HttpClient { BaseAddress = new Uri(deviceApiBaseUrl) };

IAuthClient authClient = new AuthClient(authHttp);
IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
var tokenService = new TokenService(authClient, memoryCache, gatewayClientId, gatewayClientSecret);

IDeviceServiceClient deviceClient = new DeviceServiceClient(deviceHttp, tokenService);
var cache = new MemoryCacheService();

var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

var influxLogger = loggerFactory.CreateLogger<InfluxDbService>();
var influxDbService = new InfluxDbService(influxLogger, configuration);

var influxUrl = configuration["InfluxDB:Url"] ?? "http://localhost:8087";
var influxToken = configuration["InfluxDB:Token"];
var influxOrg = configuration["InfluxDB:Org"] ?? "Wonderbiz";

var influxOptions = new InfluxDBClientOptions(influxUrl)
{
    Token = influxToken,
    Org = influxOrg
};
var influxClient = new InfluxDBClient(influxOptions);
Console.WriteLine($"InfluxDB client initialized: {influxUrl}");

var pollerLogger = loggerFactory.CreateLogger<ModbusPollerHostedService>();
var modbusPoller = new ModbusPollerHostedService(pollerLogger, configuration, cache, influxDbService);

var bridgeLogger = loggerFactory.CreateLogger<InfluxToRabbitMqBridgeService>();
var bridgeService = new InfluxToRabbitMqBridgeService(bridgeLogger, configuration, influxClient);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n⛔ Shutting down...");
    cts.Cancel();
};

try
{
    Console.WriteLine("🔑 Getting token...");
    var token = await tokenService.GetTokenAsync();

    if (string.IsNullOrEmpty(token))
    {
        Console.WriteLine("❌ Error: Failed to retrieve a valid token.");
        return;
    }

    Console.WriteLine("✅ Token acquired.");

    var configs = await deviceClient.GetConfigurationsAsync(gatewayClientId);
    Console.WriteLine($"📋 Fetched {configs.Length} configurations.");

    var configList = configs.ToList();
    cache.Set("DeviceConfigurations", configList, TimeSpan.FromMinutes(30));
    Console.WriteLine("💾 Configurations cached in memory.");
    cache.PrintCache();

    var modbusDevices = configList.Where(c => c.Protocol == 1).ToList();
    var opcuaDevices = configList.Where(c => c.Protocol == 2).ToList();

    if (opcuaDevices.Any())
        Console.WriteLine($"⚠️  Skipping {opcuaDevices.Count} OPC-UA device(s) — not implemented yet.");

    if (!modbusDevices.Any())
    {
        Console.WriteLine("⚠️  No Modbus devices found. Nothing to poll.");
        return;
    }

    Console.WriteLine($"🔄 Starting Modbus polling for {modbusDevices.Count} device(s)...");

    cache.Set("DeviceConfigurations", modbusDevices, TimeSpan.FromMinutes(30));

    // Start the Modbus poller
    await modbusPoller.StartAsync(cts.Token);
    Console.WriteLine("✅ Modbus poller initialized → will write to InfluxDB");

    // Start the Bridge service
    await bridgeService.StartAsync(cts.Token);
    Console.WriteLine("✅ Bridge service initialized → will read InfluxDB and push to RabbitMQ");

    Console.WriteLine("\n" + new string('=', 70));
    Console.WriteLine("                     EDGE GATEWAY RUNNING                     ");
    Console.WriteLine(new string('=', 70));
    Console.WriteLine("  Data Flow: Modbus → InfluxDB → RabbitMQ → Cloud");
    Console.WriteLine("  Press Ctrl+C to stop.");
    Console.WriteLine(new string('=', 70) + "\n");

    // ⭐ KEY FIX: Start the actual background execution tasks using reflection
    var modbusExecuteMethod = typeof(ModbusPollerHostedService)
        .GetMethod("ExecuteAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    var bridgeExecuteMethod = typeof(InfluxToRabbitMqBridgeService)
        .GetMethod("ExecuteAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    var modbusTask = (Task?)modbusExecuteMethod?.Invoke(modbusPoller, new object[] { cts.Token }) ?? Task.CompletedTask;
    var bridgeTask = (Task?)bridgeExecuteMethod?.Invoke(bridgeService, new object[] { cts.Token }) ?? Task.CompletedTask;

    Console.WriteLine("🚀 Background tasks started!\n");

    // Wait for cancellation
    await Task.WhenAny(
        Task.Delay(Timeout.Infinite, cts.Token),
        modbusTask,
        bridgeTask
    );
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n⛔ Gateway stopped by user.");
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"❌ HTTP error: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Unexpected error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}
finally
{
    Console.WriteLine("\n🛑 Stopping services...");

    try { await bridgeService.StopAsync(cts.Token); } catch { }
    try { await modbusPoller.StopAsync(cts.Token); } catch { }

    bridgeService.Dispose();
    influxDbService?.Dispose();
    influxClient?.Dispose();

    Console.WriteLine("✅ Services stopped and disposed.");
}