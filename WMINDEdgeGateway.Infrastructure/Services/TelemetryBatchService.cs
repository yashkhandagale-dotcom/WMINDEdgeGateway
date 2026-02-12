using WMINDEdgeGateway.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace WMINDEdgeGateway.Application.Services;

public class TelemetryBatchService
{
    private readonly ITelemetryRepository _repository;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<TelemetryBatchService> _logger;

    public TelemetryBatchService(
        ITelemetryRepository repository,
        IMessagePublisher publisher,
        ILogger<TelemetryBatchService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Starting telemetry batch retrieval...");

            // Step 1: Get pending telemetry from InfluxDB
            var batch = await _repository.GetPendingTelemetryAsync(100, cancellationToken);

            if (!batch.Any())
            {
                _logger.LogDebug("No pending telemetry records found");
                return;
            }

            _logger.LogInformation("📤 Retrieved {Count} telemetry records from InfluxDB", batch.Count);

            // Step 2: Log batch details
            foreach (var item in batch)
            {
                _logger.LogInformation(
                    "Batch Item | DeviceId={DeviceId}, SlaveId={SlaveId}, SlaveIndex={SlaveIndex}, " +
                    "Value={Value} {Unit}, Register={Register}, Type={SignalType}, Time={Timestamp:O}",
                    item.DeviceId, item.deviceSlaveId, item.slaveIndex,
                    item.Value, item.Unit, item.RegisterAddress, item.SignalType, item.Timestamp);
            }

            // Step 3: Publish to RabbitMQ
            _logger.LogInformation("Publishing {Count} records to RabbitMQ...", batch.Count);
            await _publisher.PublishBatchAsync(batch, cancellationToken);

            //Step 4: Delete from InfluxDB ONLY after successful publish
            _logger.LogInformation("Deleting {Count} records from InfluxDB...", batch.Count);
            await _repository.DeleteTelemetryBatchAsync(batch, cancellationToken);

            _logger.LogInformation("Successfully processed {Count} telemetry records", batch.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Telemetry batch service was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in telemetry batch execution");
            throw; // Re-throw to let hosted service handle retry logic
        }
    }
}