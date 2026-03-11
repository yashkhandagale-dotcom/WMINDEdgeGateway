using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using WMINDEdgeGateway.Application.Interfaces;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class TokenService
    {
        private readonly IAuthClient _authClient;
        private readonly IMemoryCache _cache;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private const string CacheKey = "GatewayAccessToken";

        public TokenService(IAuthClient authClient, IMemoryCache cache, string clientId, string clientSecret)
        {
            _authClient = authClient;
            _cache = cache;
            _clientId = clientId;
            _clientSecret = clientSecret;
        }

        public async Task<string> GetTokenAsync()
        {
            if (_cache.TryGetValue(CacheKey, out string? token))
                return token!;

            await _lock.WaitAsync();
            try
            {
                if (_cache.TryGetValue(CacheKey, out token))
                    return token!;

                var tokenResponse = await _authClient.GetTokenAsync(_clientId, _clientSecret);

                if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
                    throw new Exception("Failed to get access token from Auth service");

                // ✅ SIMPLE: Always cache for 55 minutes
                _cache.Set(CacheKey, tokenResponse.AccessToken, TimeSpan.FromMinutes(55));

                return tokenResponse.AccessToken;
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
