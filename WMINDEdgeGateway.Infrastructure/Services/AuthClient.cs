using System.Net.Http.Json;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Application.Interfaces;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Net.Http.Headers;


namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class AuthClient : IAuthClient
    {
        private readonly IHttpClientFactory _factory;

        public AuthClient(IHttpClientFactory factory) => _factory = factory;

        public async Task<AuthTokenResponse> GetTokenAsync(string clientId, string clientSecret)
        {
            var http = _factory.CreateClient("AuthClient");

            var form = new Dictionary<string, string>
    {
        { "client_id", clientId },
        { "client_secret", clientSecret }
    };

            var response = await http.PostAsync("api/devices/connect/token", new FormUrlEncodedContent(form));

            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
                throw new Exception("Failed to retrieve a valid token");

            return tokenResponse; // Return AuthTokenResponse object, not string
        }

    }
}
