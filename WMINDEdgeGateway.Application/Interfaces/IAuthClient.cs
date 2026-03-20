

using System.Threading.Tasks;
using WMINDEdgeGateway.Application.DTOs;

namespace WMINDEdgeGateway.Application.Interfaces
{
    public interface IAuthClient
    {
        Task<AuthTokenResponse> GetTokenAsync(string clientId, string clientSecret);
    }
}

