using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Application.Interfaces;
using WMINDEdgeGateway.Infrastructure.Caching;
using WMINDEdgeGateway.Infrastructure.Services;
using InfluxDB.Client;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        IConfiguration configuration = context.Configuration;

        // ✅ TOGGLE THIS FLAG FOR MOCK/REAL DATA
        bool useMockData = true; // Set to false when cloud is ready

        if (useMockData)
        {
            Console.WriteLine("⚠️  USING MOCK DATA - No cloud connection required");

            // Use Mock Device Service Client (no auth needed)
            services.AddSingleton<IDeviceServiceClient, MockDeviceServiceClient>();
        }
        else
        {
            Console.WriteLine("✓ Using real cloud API");

            // Real Auth Client
            services.AddSingleton<IAuthClient>(sp =>
            {
                var http = new HttpClient
                {
                    BaseAddress = new Uri(configuration["Services:AuthBaseUrl"]!)
                };
                return new AuthClient(http);
            });

            // Real Token Service
            services.AddSingleton<TokenService>(sp =>
            {
                var authClient = sp.GetRequiredService<IAuthClient>();
                var memoryCache = sp.GetRequiredService<IMemoryCache>();
                var clientId = configuration["Gateway:ClientId"]!;
                var clientSecret = configuration["Gateway:ClientSecret"]!;
                return new TokenService(authClient, memoryCache, clientId, clientSecret);
            });

            // Real Device Service Client
            services.AddSingleton<IDeviceServiceClient>(sp =>
            {
                var http = new HttpClient
                {
                    BaseAddress = new Uri(configuration["Services:DeviceApiBaseUrl"]!)
                };
                var tokenService = sp.GetRequiredService<TokenService>();
                return new DeviceServiceClient(http, tokenService);
            });
        }

        // Memory Cache (always needed)
        services.AddMemoryCache();
        services.AddSingleton<MemoryCacheService>();

        // InfluxDB Client
        services.AddSingleton(sp =>
        {
            var url = configuration["InfluxDB:Url"] ?? "http://localhost:8087";
            var token = configuration["InfluxDB:Token"] ?? "my-token";
            return new InfluxDBClient(url, token);
        });

        // Register all three hosted services
        services.AddHostedService<ModbusPollerHostedService>();
        services.AddHostedService<OpcUaPollerHostedService>();
        services.AddHostedService<OpcUaSubscriptionHostedService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .Build();

Console.WriteLine("=== WMIND Edge Gateway Starting ===");

// Initialize cache with configurations
await InitializeCacheAsync(host.Services);

await host.RunAsync();

async Task InitializeCacheAsync(IServiceProvider services)
{
    var deviceClient = services.GetRequiredService<IDeviceServiceClient>();
    var cache = services.GetRequiredService<MemoryCacheService>();
    var configuration = services.GetRequiredService<IConfiguration>();

    Console.WriteLine("Fetching device configurations...");

    string gatewayClientId = configuration["Gateway:ClientId"]!;

    try
    {
        var configs = await deviceClient.GetConfigurationsAsync(gatewayClientId);
        var allConfigs = (configs ?? Array.Empty<DeviceConfigurationDto>()).ToList();

        cache.Set("DeviceConfigurations", allConfigs, TimeSpan.FromMinutes(30));

        Console.WriteLine($"✓ Loaded {allConfigs.Count} device configurations into cache");

        // Count by protocol
        var modbusCount = allConfigs.Count(c => c.protocol.Equals("modbus", StringComparison.OrdinalIgnoreCase));
        var opcuaPollingCount = allConfigs.Count(c => c.protocol.Equals("opcua", StringComparison.OrdinalIgnoreCase)
            && c.opcuaMode?.Equals("polling", StringComparison.OrdinalIgnoreCase) == true);
        var opcuaPubSubCount = allConfigs.Count(c => c.protocol.Equals("opcua", StringComparison.OrdinalIgnoreCase)
            && c.opcuaMode?.Equals("pubsub", StringComparison.OrdinalIgnoreCase) == true);

        Console.WriteLine($"  - Modbus devices: {modbusCount}");
        Console.WriteLine($"  - OPC UA Polling devices: {opcuaPollingCount}");
        Console.WriteLine($"  - OPC UA PubSub devices: {opcuaPubSubCount}");

        cache.PrintCache();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ Failed to load configurations: {ex.Message}");
        throw;
    }
}