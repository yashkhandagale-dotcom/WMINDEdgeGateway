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

        /*
        services.AddSingleton<IAuthClient>(sp =>
        {
            var http = new HttpClient
            {
                BaseAddress = new Uri(configuration["Services:AuthBaseUrl"]!)
            };
            return new AuthClient(http);
        });

        services.AddSingleton<TokenService>(sp =>
        {
            var authClient = sp.GetRequiredService<IAuthClient>();
            var memoryCache = sp.GetRequiredService<IMemoryCache>();
            var clientId = configuration["Gateway:ClientId"]!;
            var clientSecret = configuration["Gateway:ClientSecret"]!;
            return new TokenService(authClient, memoryCache, clientId, clientSecret);
        });

        services.AddSingleton<IDeviceServiceClient>(sp =>
        {
            var http = new HttpClient
            {
                BaseAddress = new Uri(configuration["Services:DeviceApiBaseUrl"]!)
            };
            var tokenService = sp.GetRequiredService<TokenService>();
            return new DeviceServiceClient(http, tokenService);
        });

        services.AddMemoryCache();
        services.AddSingleton<MemoryCacheService>();

        services.AddSingleton(sp =>
        {
            var url = configuration["InfluxDB:Url"] ?? "http://localhost:8086";
            var token = configuration["InfluxDB:Token"] ?? "my-token";

            return new InfluxDBClient(url, token);
        });

        services.AddHostedService<ModbusPollerHostedService>();
        */

        services.AddHostedService<OpcUaPollerHostedService>();
        services.AddHostedService<OpcUaSubscriptionHostedService>();

    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .Build();

Console.WriteLine("OPC UA CLIENT STARTED");

/*
await InitializeCacheAsync(host.Services);
*/

await host.RunAsync();

/*
async Task InitializeCacheAsync(IServiceProvider services)
{
    var tokenService = services.GetRequiredService<TokenService>();
    var deviceClient = services.GetRequiredService<IDeviceServiceClient>();
    var cache = services.GetRequiredService<MemoryCacheService>();
    var configuration = services.GetRequiredService<IConfiguration>();

    Console.WriteLine("Edge Gateway Console App Starting...");

    string gatewayClientId = configuration["Gateway:ClientId"]!;

    var configs = await deviceClient.GetConfigurationsAsync(gatewayClientId);
    var allConfigs = (configs ?? Array.Empty<DeviceConfigurationDto>()).ToList();

    cache.Set("DeviceConfigurations", allConfigs, TimeSpan.FromMinutes(30));
    cache.PrintCache();

    Console.WriteLine("Modbus devices loaded into cache");
    Console.WriteLine($"Total devices in cache: {allConfigs.Count}");
}
*/