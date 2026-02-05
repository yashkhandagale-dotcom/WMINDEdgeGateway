public class TelemetryMessage
{
    public Guid DeviceId { get; set; }
    public Guid DeviceSlaveId { get; set; }
    public int SlaveIndex { get; set; }
    public int RegisterAddress { get; set; }
    public string SignalType { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
