using System;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
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

// Manually construct HttpClients and services — no DI host needed for this use case
var authHttp = new HttpClient { BaseAddress = new Uri(authBaseUrl) };
var deviceHttp = new HttpClient { BaseAddress = new Uri(deviceApiBaseUrl) };

IAuthClient authClient = new AuthClient(authHttp);
IDeviceServiceClient deviceClient = new DeviceServiceClient(deviceHttp);
var cache = new MemoryCacheService();

try
{
    // Step 1: Acquire token
    Console.WriteLine("Getting token...");
    var tokenResponse = await authClient.GetTokenAsync(gatewayClientId, gatewayClientSecret);

    if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
    {
        Console.WriteLine("Error: Failed to retrieve a valid token from the auth service.");
    }
    else
    {
        string token = tokenResponse.AccessToken;
        Console.WriteLine("Token acquired.");

        try
        {
            // Step 2: Fetch device configurations
            var configs = await deviceClient.GetConfigurationsAsync(gatewayClientId, token);
            Console.WriteLine($"Fetched {configs.Length} configurations.");

            // Step 3: Cache configurations
            cache.Set("DeviceConfigurations", configs, TimeSpan.FromMinutes(30));
            Console.WriteLine("Configurations cached in memory.");

            cache.PrintCache();
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("401"))
        {
            // Token expired mid-session — refresh and retry
            Console.WriteLine("Token expired. Refreshing...");
            var refreshed = await authClient.GetTokenAsync(gatewayClientId, gatewayClientSecret);
            Console.WriteLine("New token acquired.");

            var configs = await deviceClient.GetConfigurationsAsync(gatewayClientId, refreshed.AccessToken);
            cache.Set("DeviceConfigurations", configs, TimeSpan.FromMinutes(30));
            Console.WriteLine($"Fetched {configs.Length} configurations after token refresh.");

            cache.PrintCache();
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"HTTP error while fetching device configurations: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error while fetching device configurations: {ex.Message}");
        }
    }
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"HTTP error while fetching token: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"Unexpected error: {ex.Message}");
}

Console.WriteLine("Press any key to exit...");
Console.ReadKey();