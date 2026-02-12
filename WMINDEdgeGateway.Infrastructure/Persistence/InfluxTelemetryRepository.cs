using WMINDEdgeGateway.Application.Interfaces;
using WMINDEdgeGateway.Application.Models;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WMINDEdgeGateway.Infrastructure.Persistence;

public class InfluxTelemetryRepository : ITelemetryRepository
{
    private readonly InfluxDBClient _influxClient;
    private readonly string _bucket;
    private readonly string _org;
    private readonly ILogger<InfluxTelemetryRepository> _logger;

    public InfluxTelemetryRepository(
        InfluxDBClient influxClient, 
        IConfiguration configuration,
        ILogger<InfluxTelemetryRepository> logger)
    {
        _influxClient = influxClient ?? throw new ArgumentNullException(nameof(influxClient));
        _bucket = configuration["InfluxDB:Bucket"] ?? "EdgeData";
        _org = configuration["InfluxDB:Org"] ?? "WMIND";
        _logger = logger;
    }

    public async Task<List<TelemetryDto>> GetPendingTelemetryAsync(int batchSize, CancellationToken cancellationToken)
    {
       try
    {
        var query = $@"
from(bucket: ""{_bucket}"")
|> range(start: -1h)
|> filter(fn: (r) => r._measurement == ""modbus_telemetry"")
|> filter(fn: (r) => r._field == ""Value"")
|> sort(columns: [""_time""], desc: false)
|> limit(n: {batchSize})
";

        var queryApi = _influxClient.GetQueryApi();
        var tables = await queryApi.QueryAsync(query, _org, cancellationToken);

        var result = new List<TelemetryDto>();

        foreach (var table in tables)
        {
            foreach (var record in table.Records)
            {
                try
                {
                    double value = record.GetValue() switch
                    {
                        double d => d,
                        float f => (double)f,
                        int i => i,
                        long l => l,
                        _ => 0d
                    };

                    Guid deviceSlaveGuid = Guid.TryParse(
                        record.GetValueByKey("deviceSlaveId")?.ToString(),
                        out var dsGuid
                    ) ? dsGuid : Guid.Empty;

                    int slaveIndex = int.TryParse(
                        record.GetValueByKey("slaveIndex")?.ToString(),
                        out var sIdx
                    ) ? sIdx : 0;

                    var telemetry = new TelemetryDto
                    {
                        DeviceId = Guid.TryParse(
                            record.GetValueByKey("DeviceId")?.ToString(),
                            out var guid
                        ) ? guid : Guid.Empty,

                        deviceSlaveId = deviceSlaveGuid,
                        slaveIndex = slaveIndex,

                        SignalType = record.GetValueByKey("SignalType")?.ToString() ?? "unknown",

                        RegisterAddress = int.TryParse(
                            record.GetValueByKey("RegisterAddress")?.ToString(),
                            out var reg
                        ) ? reg : 0,

                        Unit = record.GetValueByKey("Unit")?.ToString() ?? "",

                        Value = value,

                        Timestamp = record.GetTime()?.ToDateTimeUtc() ?? DateTime.UtcNow
                    };

                    result.Add(telemetry);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse a record from InfluxDB");
                }
            }
        }

        _logger.LogInformation("Retrieved {Count} telemetry records from InfluxDB", result.Count);
        return result;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error querying InfluxDB for telemetry");
        return new List<TelemetryDto>();
    }
    }

    public async Task DeleteTelemetryBatchAsync(IEnumerable<TelemetryDto> batch, CancellationToken cancellationToken)
    {
        if (!batch.Any()) return;

        try
        {
            var startTime = batch.Min(p => p.Timestamp);
            var stopTime = batch.Max(p => p.Timestamp).AddSeconds(1);

            var deviceIds = batch.Select(p => p.DeviceId).Distinct();

            foreach (var deviceId in deviceIds)
            {
                 _influxClient.GetDeleteApi().Delete(
                    start:                          ,
                    stop: stopTime,
                    predicate: $"_measurement=\"modbus_telemetry\" AND DeviceId=\"{deviceId}\"",
                    bucket: _bucket,
                    org: _org,
                    cancellationToken: cancellationToken
                );

                _logger.LogInformation("Deleted telemetry for device {DeviceId}", deviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete telemetry batch from InfluxDB");
            throw; // Let caller handle deletion failures
        }
    }
}