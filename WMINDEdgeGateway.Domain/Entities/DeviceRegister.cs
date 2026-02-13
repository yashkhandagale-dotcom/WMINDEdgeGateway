namespace WMINDEdgeGateway.Domain.Entities;

public class DeviceRegister
{
    public Guid RegisterId { get; set; }
    public int RegisterAddress { get; set; } // 40003 etc
    public int RegisterLength { get; set; }  // 2
    public string DataType { get; set; } = "";
    public double Scale { get; set; }
    public string Unit { get; set; } = "";
    public string ByteOrder { get; set; } = "Little";
    public bool WordSwap { get; set; }
}
