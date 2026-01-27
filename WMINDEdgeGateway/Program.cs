using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WMINDEdgeGateway.Application.Interfaces;
using WMINDEdgeGateway.Infrastructure.Caching;
using WMINDEdgeGateway.Infrastructure.Services;

Console.WriteLine("Edge Gateway Starting...");

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(config =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    })
    .ConfigureServices((context, services) =>
    {
        // HttpClients
        services.AddHttpClient("AuthClient", c =>
        {
            var baseUrl = context.Configuration["Auth:BaseUrl"];
            if (!string.IsNullOrEmpty(baseUrl))
                c.BaseAddress = new Uri(baseUrl);
        });

        services.AddHttpClient("DeviceClient", c =>
        {
            var baseUrl = context.Configuration["DeviceApi:BaseUrl"];
            if (!string.IsNullOrEmpty(baseUrl))
                c.BaseAddress = new Uri(baseUrl);
        });

        // Services
        services.AddSingleton<IAuthClient, AuthClient>();
        services.AddSingleton<IDeviceServiceClient, DeviceServiceClient>();
        services.AddSingleton<IMemoryCacheService, MemoryCacheService>();

        // Options
        services.Configure<GatewayOptions>(context.Configuration.GetSection("Gateway"));
        services.Configure<CacheOptions>(context.Configuration.GetSection("Cache"));
    })
    .Build();

// Resolve services
using var scope = host.Services.CreateScope();
var services = scope.ServiceProvider;

var gatewayOptions = services.GetRequiredService<IOptions<GatewayOptions>>().Value;
var cacheOptions = services.GetRequiredService<IOptions<CacheOptions>>().Value;

var authClient = services.GetRequiredService<IAuthClient>();
var deviceClient = services.GetRequiredService<IDeviceServiceClient>();
var cache = services.GetRequiredService<IMemoryCacheService>();

// Run gateway once
await RunGatewayOnceAsync(authClient, deviceClient, cache, gatewayOptions, cacheOptions);

Console.WriteLine("Press any key to exit...");
Console.ReadKey();


// ============================
// Gateway Execution Logic (Single Fetch)
// ============================
static async Task RunGatewayOnceAsync(
    IAuthClient authClient,
    IDeviceServiceClient deviceClient,
    IMemoryCacheService cache,
    GatewayOptions gatewayOptions,
    CacheOptions cacheOptions)
{
    try
    {
        // Get token
        Console.WriteLine("Getting token...");
        var tokenResponse = await authClient.GetTokenAsync(
            gatewayOptions.ClientId,
            gatewayOptions.ClientSecret
        );
        string token = tokenResponse.AccessToken;
        Console.WriteLine("Token acquired.");

        // Fetch device configurations
        var configs = await deviceClient.GetConfigurationsAsync(
            gatewayOptions.ClientId,
            token
        );

        // Cache the configurations
        cache.Set("DeviceConfigurations", configs, TimeSpan.FromMinutes(cacheOptions.ConfigurationsMinutes));
        Console.WriteLine($"Fetched and cached {configs.Length} configurations.");

        // Print cached devices
        cache.PrintCache();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}
