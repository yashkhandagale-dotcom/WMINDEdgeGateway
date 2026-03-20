using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WMINDEdgeGateway.Application.DTOs;

namespace WMINDEdgeGateway.Application.Interfaces
{
    public interface IDeviceServiceClient
    {
        Task<DeviceConfigurationDto[]> GetConfigurationsAsync(string gatewayId);
    }
}


