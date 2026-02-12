using System;

namespace WMINDEdgeGateway.Application.Models;

public class TelemetryDto
{
    public Guid DeviceId { get; set; }
    public Guid deviceSlaveId { get; set; }
    public int slaveIndex { get; set; }
    public string SignalType { get; set; } = string.Empty;
    public double Value { get; set; }
    public int RegisterAddress { get; set; }
    public string Unit { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}