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

    // ===========================
    // Modbus Register DTO
    // ===========================
    public class DeviceRegisterDto
    {
        [JsonPropertyName("registerId")]
        public Guid RegisterId { get; set; }

        [JsonPropertyName("registerAddress")]
        public int RegisterAddress { get; set; }

        [JsonPropertyName("registerLength")]
        public int RegisterLength { get; set; }

        [JsonPropertyName("dataType")]
        public string DataType { get; set; } = string.Empty;

        [JsonPropertyName("scale")]
        public double Scale { get; set; }

        [JsonPropertyName("unit")]
        public string? Unit { get; set; }

        [JsonPropertyName("byteOrder")]
        public string? ByteOrder { get; set; }

        [JsonPropertyName("wordSwap")]
        public bool WordSwap { get; set; }

        [JsonPropertyName("signalId")]
        public Guid? SignalId { get; set; }

        [JsonPropertyName("isHealthy")]
        public bool IsHealthy { get; set; }
    }

    // ===========================
    // Modbus Slave DTO
    // ===========================
    public class DeviceSlaveDto
    {
        [JsonPropertyName("deviceSlaveId")]
        public Guid DeviceSlaveId { get; set; }

        [JsonPropertyName("slaveIndex")]
        public int SlaveIndex { get; set; }

        [JsonPropertyName("isHealthy")]
        public bool IsHealthy { get; set; }

        [JsonPropertyName("registers")]
        public DeviceRegisterDto[] Registers { get; set; } = Array.Empty<DeviceRegisterDto>();
    }

    // ===========================
    // OPC UA Node DTO (FIXED)
    // ===========================
    public class OpcUaNodeDto
    {
        [JsonPropertyName("opcUaNodeId")]
        public Guid OpcUaNodeId { get; set; }

        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("signalId")]
        public Guid? SignalId { get; set; }

        [JsonPropertyName("signalName")]
        public string SignalName { get; set; } = string.Empty;

        [JsonPropertyName("dataType")]
        public string DataType { get; set; } = string.Empty;

        [JsonPropertyName("unit")]
        public string? Unit { get; set; }

        [JsonPropertyName("scalingFactor")]
        public double ScalingFactor { get; set; }

        [JsonPropertyName("signalTypeId")]
        public Guid? SignalTypeId { get; set; }
    }

    // ===========================
    // Device Configuration DTO
    // ===========================
    public class DeviceConfigurationDto
    {
        [JsonPropertyName("deviceId")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string DeviceName { get; set; } = string.Empty;

        [JsonPropertyName("protocol")]
        public int Protocol { get; set; }

        [JsonPropertyName("opcUaMode")]
        public string? OpcUaMode { get; set; }

        [JsonPropertyName("pollIntervalMs")]
        public int? PollIntervalMs { get; set; }

        [JsonPropertyName("connectionString")]
        public string? ConnectionString { get; set; }

        [JsonPropertyName("connectionMode")]
        public int? ConnectionMode { get; set; }

        [JsonPropertyName("ipAddress")]
        public string? IpAddress { get; set; }

        [JsonPropertyName("port")]
        public int? Port { get; set; }

        [JsonPropertyName("slaveId")]
        public int? SlaveId { get; set; }

        [JsonPropertyName("endian")]
        public string? Endian { get; set; }

        [JsonPropertyName("slaves")]
        public DeviceSlaveDto[] Slaves { get; set; } = Array.Empty<DeviceSlaveDto>();

        [JsonPropertyName("opcUaNodes")]
        public OpcUaNodeDto[] OpcUaNodes { get; set; } = Array.Empty<OpcUaNodeDto>();
    }
}
