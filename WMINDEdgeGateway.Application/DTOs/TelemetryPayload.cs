// Application/DTOs/TelemetryPayload.cs
namespace WMINDEdgeGateway.Application.DTOs
{
    public record TelemetryPayload(
        string SignalId,
        double Value,
        DateTime Timestamp
    );
}