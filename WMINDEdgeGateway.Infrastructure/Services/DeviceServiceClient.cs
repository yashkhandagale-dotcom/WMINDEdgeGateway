using System.Net.Http.Headers;
using System.Net.Http.Json;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Application.Interfaces;
using System.Net.Http;
using System.Net.Http.Headers;


namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class DeviceServiceClient : IDeviceServiceClient
    {
        private readonly IHttpClientFactory _factory;

        public DeviceServiceClient(IHttpClientFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<DeviceConfigurationDto[]> GetConfigurationsAsync(string gatewayId, string token)
        {
            var http = _factory.CreateClient("DeviceClient");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await http.GetAsync($"api/devices/devices/configurations/gateway/{gatewayId}");
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse<DeviceConfigurationDto[]>>();
            if (apiResponse == null || !apiResponse.Success)
                throw new Exception($"API returned error: {apiResponse?.Error}");

            return apiResponse.Data ?? Array.Empty<DeviceConfigurationDto>();
        }
    }

}
