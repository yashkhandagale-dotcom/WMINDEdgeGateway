using Xunit;
using FluentAssertions;
using WMINDEdgeGateway.Infrastructure.Services;

namespace WMINDEdgeGateway.Tests.Services
{
    public class ModbusRegisterGroupingTests
    {
        // ════════════════════════════════════════════════════
        //  Single group
        // ════════════════════════════════════════════════════

        [Fact]
        public void GroupContiguous_AllContiguous_ShouldReturnOneGroup()
        {
            var addresses = new[] { 0, 1, 2, 3 };

            var groups = ModbusRegisterGrouping
                .GroupContiguous(addresses, r => r)
                .ToList();

            groups.Should().HaveCount(1);
            groups[0].Should().HaveCount(4);
        }

        [Fact]
        public void GroupContiguous_SingleItem_ShouldReturnOneGroup()
        {
            var addresses = new[] { 42 };

            var groups = ModbusRegisterGrouping
                .GroupContiguous(addresses, r => r)
                .ToList();

            groups.Should().HaveCount(1);
            groups[0].Should().ContainSingle()
                .Which.Should().Be(42);
        }

        // ════════════════════════════════════════════════════
        //  Multiple groups
        // ════════════════════════════════════════════════════

        [Fact]
        public void GroupContiguous_OneGap_ShouldReturnTwoGroups()
        {
            // 0,1 are contiguous | gap | 5,6 are contiguous
            var addresses = new[] { 0, 1, 5, 6 };

            var groups = ModbusRegisterGrouping
                .GroupContiguous(addresses, r => r)
                .ToList();

            groups.Should().HaveCount(2);
            groups[0].Should().BeEquivalentTo(new[] { 0, 1 });
            groups[1].Should().BeEquivalentTo(new[] { 5, 6 });
        }

        [Fact]
        public void GroupContiguous_AllSeparate_ShouldReturnGroupPerItem()
        {
            // Every other address — no two are contiguous
            var addresses = new[] { 0, 2, 4, 6 };

            var groups = ModbusRegisterGrouping
                .GroupContiguous(addresses, r => r)
                .ToList();

            groups.Should().HaveCount(4);
            foreach (var g in groups)
                g.Should().HaveCount(1);
        }

        [Fact]
        public void GroupContiguous_ThreeGroups_ShouldSplitCorrectly()
        {
            var addresses = new[] { 0, 1, 10, 11, 20, 21, 22 };

            var groups = ModbusRegisterGrouping
                .GroupContiguous(addresses, r => r)
                .ToList();

            groups.Should().HaveCount(3);
            groups[0].Should().BeEquivalentTo(new[] { 0, 1 });
            groups[1].Should().BeEquivalentTo(new[] { 10, 11 });
            groups[2].Should().BeEquivalentTo(new[] { 20, 21, 22 });
        }

        // ════════════════════════════════════════════════════
        //  Ordering — unsorted input
        // ════════════════════════════════════════════════════

        [Fact]
        public void GroupContiguous_UnsortedInput_ShouldSortAndGroupCorrectly()
        {
            // Input is out of order — method should sort first
            var addresses = new[] { 3, 1, 2, 0 };

            var groups = ModbusRegisterGrouping
                .GroupContiguous(addresses, r => r)
                .ToList();

            groups.Should().HaveCount(1);
            groups[0].Should().HaveCount(4);
        }

        // ════════════════════════════════════════════════════
        //  Edge cases
        // ════════════════════════════════════════════════════

        [Fact]
        public void GroupContiguous_EmptyInput_ShouldReturnNoGroups()
        {
            var addresses = Array.Empty<int>();

            var groups = ModbusRegisterGrouping
                .GroupContiguous(addresses, r => r)
                .ToList();

            groups.Should().BeEmpty();
        }

        [Fact]
        public void GroupContiguous_LargeContiguousRange_ShouldReturnOneGroup()
        {
            // 100 contiguous addresses
            var addresses = Enumerable.Range(0, 100).ToArray();

            var groups = ModbusRegisterGrouping
                .GroupContiguous(addresses, r => r)
                .ToList();

            groups.Should().HaveCount(1);
            groups[0].Should().HaveCount(100);
        }

        // ════════════════════════════════════════════════════
        //  Real-world scenario — DeviceRegisterDto style
        // ════════════════════════════════════════════════════

        [Fact]
        public void GroupContiguous_PlcZeroBasedAddresses_ShouldGroupCorrectly()
        {
            // Simulating: 40001→0, 40002→1, 40005→4, 40006→5
            // Expected: group1=[0,1], group2=[4,5]
            var addresses = new[] { 0, 1, 4, 5 };

            var groups = ModbusRegisterGrouping
                .GroupContiguous(addresses, r => r)
                .ToList();

            groups.Should().HaveCount(2);
            groups[0].Should().BeEquivalentTo(new[] { 0, 1 });
            groups[1].Should().BeEquivalentTo(new[] { 4, 5 });
        }

        [Fact]
        public void GroupContiguous_AddrSelector_WorksWithCustomObject()
        {
            // Test with a real object that has an address property
            var registers = new[]
            {
                new { Address = 10, Name = "TempSensor" },
                new { Address = 11, Name = "PressureSensor" },
                new { Address = 15, Name = "FlowSensor" }
            };

            var groups = ModbusRegisterGrouping
                .GroupContiguous(registers, r => r.Address)
                .ToList();

            groups.Should().HaveCount(2);
            groups[0].Should().HaveCount(2); // Address 10, 11
            groups[1].Should().HaveCount(1); // Address 15
        }
    }
}