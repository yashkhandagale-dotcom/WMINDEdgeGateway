using System.Net.Http.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Application.Interfaces;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class AuthClient : IAuthClient
    {
        private readonly HttpClient _http;

        // FIX: Replace IHttpClientFactory with a direct HttpClient parameter.
        // AddHttpClient<IAuthClient, AuthClient>() in Program.cs requires this
        // constructor signature to inject the pre-configured typed client.
        public AuthClient(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public async Task<AuthTokenResponse> GetTokenAsync(string clientId, string clientSecret)
        {
            var form = new Dictionary<string, string>
            {
                { "client_id",     clientId     },
                { "client_secret", clientSecret }
            };

            // Per-request message — same thread-safe pattern as DeviceServiceClient
            var request = new HttpRequestMessage(HttpMethod.Post, "api/devices/connect/token")
            {
                Content = new FormUrlEncodedContent(form)
            };

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();

            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
                throw new Exception("Failed to retrieve a valid token");

            return tokenResponse;
        }
    }
}