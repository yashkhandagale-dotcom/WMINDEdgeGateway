namespace WMINDEdgeGateway.Domain.Entities;

public class DeviceSlave
{
    public Guid DeviceSlaveId { get; set; }
    public int SlaveIndex { get; set; }
    public bool IsHealthy { get; set; }
    public List<DeviceRegister> Registers { get; set; } = [];
}
