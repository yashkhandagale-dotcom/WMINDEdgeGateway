using WMINDEdgeGateway.Application.Models;

namespace WMINDEdgeGateway.Application.Interfaces;

public interface ITelemetryRepository
{
    Task<List<TelemetryDto>> GetPendingTelemetryAsync(
        int batchSize,
        CancellationToken cancellationToken
    );
    Task DeleteTelemetryBatchAsync(IEnumerable<TelemetryDto> batch, CancellationToken cancellationToken);
}
