using System;
using System.Threading.Tasks;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Application.Interfaces;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class MockDeviceServiceClient : IDeviceServiceClient
    {
        public Task<DeviceConfigurationDto[]> GetConfigurationsAsync(string gatewayId)
        {
            Console.WriteLine($"[MOCK] Returning test data for gateway: {gatewayId}");

            var mockConfigs = new[]
            {
                // Modbus Device
                new DeviceConfigurationDto(
                    Id: new Guid("dc9e861c-6601-48b9-a971-5af538de4cbb"),
                    deviceName: "Assembly Line Modbus RTU",
                    protocol: "modbus",
                    opcuaMode: null,
                    pollIntervalMs: 1000,
                    connectionString: "127.0.0.1:5020",
                    configurationJson: null,
                    slaves: new[]
                    {
                        new DeviceSlaveDto(
                            deviceSlaveId: new Guid("e5981bf3-935d-4a7a-bc78-185ac4662eb2"),
                            slaveIndex: 1,
                            isHealthy: true,
                            registers: new[]
                            {
                                new DeviceRegisterDto(
                                    registerId: new Guid("33f4234d-5886-4735-8cb3-edc90db42541"),
                                    registerAddress: 40001,
                                    registerLength: 2,
                                    dataType: "float32",
                                    scale: 1.0,
                                    unit: "V",
                                    byteOrder: "Big",
                                    wordSwap: false,
                                    isHealthy: true
                                )
                            }
                        )
                    },
                    opcuaNodes: null
                ),

                // OPC UA Polling Device
                new DeviceConfigurationDto(
                    Id: new Guid("639f1a53-7078-41bb-80c6-144db9322699"),
                    deviceName: "CNC Machine OPC UA - Polling",
                    protocol: "opcua",
                    opcuaMode: "polling",
                    pollIntervalMs: 2000,
                    connectionString: "opc.tcp://localhost:4840/Simulator",
                    configurationJson: null,
                    slaves: null,
                    opcuaNodes: new[]
                    {
                        new OpcUaNodeDto(
                            nodeId: "ns=2;s=Plant=MUMBAI_PLANT/Line=ASSEMBLY_01/Machine=CNC_02/Signal=VOLTAGE",
                            displayName: "Voltage",
                            dataType: "Double",
                            unit: "V",
                            isHealthy: true
                        ),
                        new OpcUaNodeDto(
                            nodeId: "ns=2;s=Plant=MUMBAI_PLANT/Line=ASSEMBLY_01/Machine=CNC_02/Signal=TEMP",
                            displayName: "Temperature",
                            dataType: "Double",
                            unit: "°C",
                            isHealthy: true
                        ),
                        new OpcUaNodeDto(
                            nodeId: "ns=2;s=Plant=MUMBAI_PLANT/Line=ASSEMBLY_01/Machine=CNC_02/Signal=RPM",
                            displayName: "RPM",
                            dataType: "Double",
                            unit: "rpm",
                            isHealthy: true
                        )
                    }
                ),

                // OPC UA PubSub Device
                new DeviceConfigurationDto(
                    Id: new Guid("f70622bd-edbd-49a9-abb8-360c98b76cea"),
                    deviceName: "Conveyor Belt OPC UA - PubSub",
                    protocol: "opcua",
                    opcuaMode: "pubsub",
                    pollIntervalMs: 0,
                    connectionString: "opc.tcp://localhost:4840/Simulator",
                    configurationJson: null,
                    slaves: null,
                    opcuaNodes: new[]
                    {
                        new OpcUaNodeDto(
                            nodeId: "ns=2;s=Plant=MUMBAI_PLANT/Line=ASSEMBLY_01/Machine=CNC_02/Signal=VOLTAGE",
                            displayName: "Belt Speed",
                            dataType: "Double",
                            unit: "m/s",
                            isHealthy: true
                        )
                    }
                )
            };

            return Task.FromResult(mockConfigs);
        }
    }
}