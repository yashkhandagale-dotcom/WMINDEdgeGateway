using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

            // FIX: Corrected route to match actual server-registered URL (confirmed via Postman).
            // No leading slash — lets BaseAddress ("http://localhost:5000/") resolve correctly.
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/devices/devices/configurations/gateway/{gatewayId}"
            );
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse<DeviceConfigurationDto[]>>();

            if (apiResponse == null)
                return Array.Empty<DeviceConfigurationDto>();

            if (!apiResponse.Success)
                throw new Exception($"API returned error: {apiResponse.Error}");

            return apiResponse.Data ?? Array.Empty<DeviceConfigurationDto>();
        }
    }
}