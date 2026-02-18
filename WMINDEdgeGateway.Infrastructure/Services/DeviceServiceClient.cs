using System;
using System.Linq;
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

        // MOCK URL
        private const string MOCK_OPC_URL =
            "https://6995446ab081bc23e9c289d0.mockapi.io/api/getdevices/DEVICE-CONFIG";

        public DeviceServiceClient(HttpClient http, TokenService tokenService)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        }

        public async Task<DeviceConfigurationDto[]> GetConfigurationsAsync(string gatewayId)
        {
            if (string.IsNullOrWhiteSpace(gatewayId))
                throw new ArgumentException("Gateway ID cannot be empty", nameof(gatewayId));

            //GET TOKEN
            var token = await _tokenService.GetTokenAsync();

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("TokenService returned an empty token.");

            // GET BACKEND CONFIGS (MODBUS)
            DeviceConfigurationDto[] backendConfigs = Array.Empty<DeviceConfigurationDto>();

            try
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"api/devices/devices/configurations/gateway/{gatewayId}"
                );

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                var response = await _http.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var apiResponse =
                    await response.Content.ReadFromJsonAsync<ApiResponse<DeviceConfigurationDto[]>>();

                if (apiResponse != null && apiResponse.Success && apiResponse.Data != null)
                    backendConfigs = apiResponse.Data;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Backend config fetch failed: {ex.Message}");
            }

            // GET MOCK OPC CONFIGS
            DeviceConfigurationDto[] mockConfigs = Array.Empty<DeviceConfigurationDto>();

            try
            {
                using var mockHttp = new HttpClient();

                var result =
                    await mockHttp.GetFromJsonAsync<DeviceConfigurationDto[]>(MOCK_OPC_URL);

                if (result != null)
                    mockConfigs = result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mock OPC fetch failed: {ex.Message}");
            }

            // MERGE BOTH
            var merged = backendConfigs
                .Concat(mockConfigs)
                .ToArray();

            Console.WriteLine(
                $"Loaded configs → Backend:{backendConfigs.Length} + MockOPC:{mockConfigs.Length} = Total:{merged.Length}");

            return merged;
        }
    }
}
