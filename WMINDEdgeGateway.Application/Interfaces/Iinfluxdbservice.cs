// Application/Interfaces/IInfluxDbService.cs
using WMINDEdgeGateway.Application.DTOs;

namespace WMINDEdgeGateway.Application.Interfaces
{
    public interface IInfluxDbService
    {
        Task WriteAsync(IEnumerable<TelemetryPayload> payloads,
                        CancellationToken cancellationToken = default);
    }
}