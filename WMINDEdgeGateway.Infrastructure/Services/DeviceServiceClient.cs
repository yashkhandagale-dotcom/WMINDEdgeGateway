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
        private readonly TokenService _tokenService;

        public DeviceServiceClient(HttpClient http, TokenService tokenService)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        }

        public async Task<DeviceConfigurationDto[]> GetConfigurationsAsync(string gatewayId)
        {
            if (string.IsNullOrWhiteSpace(gatewayId))
                throw new ArgumentException("Gateway ID cannot be empty", nameof(gatewayId));

            // Get token from TokenService instead of accepting it as a parameter
            var token = await _tokenService.GetTokenAsync();

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("TokenService returned an empty token.");

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
                throw new Exception($"API Error: {apiResponse.Error}");

            return apiResponse.Data ?? Array.Empty<DeviceConfigurationDto>();
        }
    }
}