using System;
using System.Net.Http;
using WMINDEdgeGateway.Application.Interfaces;
using WMINDEdgeGateway.Infrastructure.Caching;
using WMINDEdgeGateway.Infrastructure.Services;

Console.WriteLine("Edge Gateway Console App Starting...");


string gatewayClientId = "GW-8c96595a802a40ccb80a0a6f480638d6";
string gatewayClientSecret = "mgdk9LpTvPHNvvMpD6Oys7TkUCj8Q4qFnjdruVDDPnY=";


string authBaseUrl = "http://localhost:5000";       
string deviceApiBaseUrl = "http://localhost:5000";

var authHttp = new HttpClient { BaseAddress = new Uri(authBaseUrl) };
var deviceHttp = new HttpClient { BaseAddress = new Uri(deviceApiBaseUrl) };

IAuthClient authClient = new AuthClient(authHttp);
IDeviceServiceClient deviceClient = new DeviceServiceClient(deviceHttp);
var cache = new MemoryCacheService();

try
{
    var tokenResponse = await authClient.GetTokenAsync(gatewayClientId, gatewayClientSecret);
    if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
    {
        Console.WriteLine("Error: Failed to retrieve a valid token from the auth service.");
    }
    else
    {
        string token = tokenResponse.AccessToken;
        Console.WriteLine($"Token received: {token}");

        try
        {
            var configs = await deviceClient.GetConfigurationsAsync(gatewayClientId, token);
            Console.WriteLine($"Fetched {configs.Length} configurations");

            cache.Set("DeviceConfigurations", configs, TimeSpan.FromMinutes(30));
            Console.WriteLine("Configurations cached in memory");
        }
        catch (HttpRequestException httpEx)
        {
            Console.WriteLine($"HTTP error while fetching device configurations: {httpEx.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error while fetching device configurations: {ex.Message}");
        }
    }
}
catch (HttpRequestException httpEx)
{
    Console.WriteLine($"HTTP error while fetching token: {httpEx.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"Unexpected error while fetching token: {ex.Message}");
}

Console.WriteLine("Press any key to exit...");
Console.ReadKey();
