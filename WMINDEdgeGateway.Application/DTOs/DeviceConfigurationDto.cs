using System;
using System.Text.Json.Serialization;

namespace WMINDEdgeGateway.Application.DTOs
{
    public class ApiResponse<T>
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public T? Data { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    public record DeviceRegisterDto(
        [property: JsonPropertyName("registerId")] Guid registerId,
        [property: JsonPropertyName("registerAddress")] int registerAddress,
        [property: JsonPropertyName("registerLength")] int registerLength,
        [property: JsonPropertyName("dataType")] string dataType,
        [property: JsonPropertyName("scale")] double scale,
        [property: JsonPropertyName("unit")] string? unit,
        [property: JsonPropertyName("byteOrder")] string byteOrder,
        [property: JsonPropertyName("wordSwap")] bool wordSwap,
        [property: JsonPropertyName("isHealthy")] bool isHealthy
    );

    public record DeviceSlaveDto(
        [property: JsonPropertyName("deviceSlaveId")] Guid deviceSlaveId,
        [property: JsonPropertyName("slaveIndex")] int slaveIndex,
        [property: JsonPropertyName("isHealthy")] bool isHealthy,
        [property: JsonPropertyName("registers")] DeviceRegisterDto[] registers
    );

    // NEW: OPC UA Node DTO
    public record OpcUaNodeDto(
        [property: JsonPropertyName("nodeId")] string nodeId,
        [property: JsonPropertyName("displayName")] string displayName,
        [property: JsonPropertyName("dataType")] string dataType,
        [property: JsonPropertyName("unit")] string? unit,
        [property: JsonPropertyName("isHealthy")] bool isHealthy
    );

    // UPDATED: Main Configuration DTO with OPC UA support
    public record DeviceConfigurationDto(
        [property: JsonPropertyName("deviceId")] Guid Id,
        [property: JsonPropertyName("name")] string deviceName,
        [property: JsonPropertyName("protocol")] string protocol,
        [property: JsonPropertyName("opcuaMode")] string? opcuaMode,
        [property: JsonPropertyName("pollIntervalMs")] int pollIntervalMs,
        [property: JsonPropertyName("connectionString")] string? connectionString,
        [property: JsonPropertyName("protocolSettingsJson")] string? configurationJson,
        [property: JsonPropertyName("slaves")] DeviceSlaveDto[]? slaves,
        [property: JsonPropertyName("opcuaNodes")] OpcUaNodeDto[]? opcuaNodes
    );
}