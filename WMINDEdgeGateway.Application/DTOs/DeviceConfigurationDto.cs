using System;
using System.Text.Json.Serialization;

namespace WMINDEdgeGateway.Application.DTOs
{
    public class ApiResponse<T>
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public T Data { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    public record DeviceRegisterDto(
        [property: JsonPropertyName("registerId")] Guid RegisterId,
        [property: JsonPropertyName("registerAddress")] int RegisterAddress,
        [property: JsonPropertyName("registerLength")] int RegisterLength,
        [property: JsonPropertyName("dataType")] string DataType,
        [property: JsonPropertyName("scale")] double Scale,
        [property: JsonPropertyName("unit")] string? Unit,
        [property: JsonPropertyName("byteOrder")] string ByteOrder,
        [property: JsonPropertyName("wordSwap")] bool WordSwap,
        [property: JsonPropertyName("isHealthy")] bool IsHealthy
    );

    public record DeviceSlaveDto(
        [property: JsonPropertyName("deviceSlaveId")] Guid DeviceSlaveId,
        [property: JsonPropertyName("slaveIndex")] int SlaveIndex,
        [property: JsonPropertyName("isHealthy")] bool IsHealthy,
        [property: JsonPropertyName("registers")] DeviceRegisterDto[] Registers
    );

    public record DeviceConfigurationDto(
        [property: JsonPropertyName("deviceId")] Guid Id,
        [property: JsonPropertyName("name")] string DeviceName,
        [property: JsonPropertyName("protocol")] string Protocol,
        [property: JsonPropertyName("pollIntervalMs")] int PollIntervalMs,
        [property: JsonPropertyName("protocolSettingsJson")] string ConfigurationJson,
        [property: JsonPropertyName("slaves")] DeviceSlaveDto[] Slaves
    );
}