using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WMINDEdgeGateway.Infrastructure.Services;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    /// <summary>
    /// Service interface for writing telemetry data to InfluxDB
    /// </summary>
    public interface IInfluxDbService
    {
        /// <summary>
        /// Writes a batch of telemetry payloads to InfluxDB
        /// </summary>
        /// <param name="payloads">Collection of telemetry data points</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task WriteAsync(IEnumerable<TelemetryPayload> payloads, CancellationToken cancellationToken = default);
    }
}