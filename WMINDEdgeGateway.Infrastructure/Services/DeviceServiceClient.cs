using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Application.Interfaces;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class DeviceServiceClient : IDeviceServiceClient
    {
        private readonly HttpClient _http;

        public DeviceServiceClient(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public async Task<DeviceConfigurationDto[]> GetConfigurationsAsync(string gatewayId, string token)
        {
            if (string.IsNullOrWhiteSpace(gatewayId))
                throw new ArgumentException("Gateway ID cannot be empty", nameof(gatewayId));
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token cannot be empty", nameof(token));

            // FIX 4: Build a per-request message instead of mutating DefaultRequestHeaders.
            // Mutating DefaultRequestHeaders is not thread-safe and causes race conditions
            // when multiple requests are in-flight concurrently.
            // FIX 5: Added leading slash to the relative URI.
            // Without it, Uri resolution can silently drop the last segment of BaseAddress
            // (e.g. "http://host/api/" + "api/devices/..." works but
            //        "http://host/api"  + "api/devices/..." resolves to "http://host/api/devices/...")
            // A leading slash makes the path absolute relative to the host, which is unambiguous.
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/devices/configurations/gateway/{gatewayId}"
            );
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            // Read raw JSON first for diagnostic logging
            var rawJson = await response.Content.ReadAsStringAsync();
            Console.WriteLine("Raw JSON Response:");
            Console.WriteLine(rawJson);
            Console.WriteLine(new string('=', 80));

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            };

            try
            {
                var apiResponse = JsonSerializer.Deserialize<ApiResponse<DeviceConfigurationDto[]>>(rawJson, options);

                if (apiResponse == null)
                {
                    Console.WriteLine("API response is null");
                    return Array.Empty<DeviceConfigurationDto>();
                }

                if (!apiResponse.Success)
                {
                    Console.WriteLine($"API returned error: {apiResponse.Error}");
                    throw new Exception($"API returned error: {apiResponse.Error}");
                }

                Console.WriteLine($"Successfully deserialized {apiResponse.Data?.Length ?? 0} configurations");
                return apiResponse.Data ?? Array.Empty<DeviceConfigurationDto>();
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"JSON Deserialization Error:");
                Console.WriteLine($"   Message: {ex.Message}");
                Console.WriteLine($"   Path: {ex.Path}");
                Console.WriteLine($"   Line: {ex.LineNumber}, Position: {ex.BytePositionInLine}");
                Console.WriteLine();
                Console.WriteLine("Raw JSON was:");
                Console.WriteLine(rawJson);
                throw;
            }
        }
    }
}