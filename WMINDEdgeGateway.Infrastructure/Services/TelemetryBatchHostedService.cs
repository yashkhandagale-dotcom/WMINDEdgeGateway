using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WMINDEdgeGateway.Application.Services;

public class TelemetryBatchHostedService : BackgroundService
{
    private readonly TelemetryBatchService _batchService;
    private readonly ILogger<TelemetryBatchHostedService> _logger;

    public TelemetryBatchHostedService(
        TelemetryBatchService batchService,
        ILogger<TelemetryBatchHostedService> logger)
    {
        _batchService = batchService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telemetry batch sender started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _batchService.ExecuteAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telemetry batch send failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
