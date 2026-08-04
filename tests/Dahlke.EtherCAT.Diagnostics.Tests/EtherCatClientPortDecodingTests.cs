using Dahlke.EtherCAT.Diagnostics;
using FluentAssertions;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

/// <summary>
/// The EtherCAT master returns one uint32 CRC counter per *linked* port from IG 0x12, so the
/// response length is the port count. Byte lengths here are the ones observed on a real
/// EK1100 + 7-terminal bus: 8 bytes for the chained slaves, 4 for the trailing EL2808.
/// </summary>
public class EtherCatClientPortDecodingTests
{
    [Theory]
    [InlineData(0, 0)]   // read failed
    [InlineData(3, 0)]   // short read, not even one counter
    [InlineData(4, 1)]   // last slave in the chain
    [InlineData(8, 2)]   // slave with upstream + downstream link
    [InlineData(12, 3)]  // junction (e.g. EK1122)
    [InlineData(16, 4)]
    [InlineData(32, 4)]  // never report more ports than an ESC has
    public void CountReportedPorts_derives_port_count_from_response_length(int byteCount, int expected)
    {
        EtherCatClient.CountReportedPorts(new byte[byteCount]).Should().Be(expected);
    }

    [Fact]
    public void CountReportedPorts_treats_a_missing_block_as_no_ports()
    {
        EtherCatClient.CountReportedPorts(null).Should().Be(0);
    }

    [Fact]
    public void BuildPortInfo_marks_two_ports_linked_for_a_chained_slave()
    {
        var ports = EtherCatClient.BuildPortInfo(new byte[8]);

        ports.Should().HaveCount(4);
        ports.Should().SatisfyRespectively(
            a => { a.Port.Should().Be("A"); a.LinkState.Should().BeTrue(); a.Configured.Should().BeTrue(); a.Physic.Should().Be("EBus"); },
            b => { b.Port.Should().Be("B"); b.LinkState.Should().BeTrue(); b.Configured.Should().BeTrue(); b.Physic.Should().Be("EBus"); },
            c => { c.Port.Should().Be("C"); c.LinkState.Should().BeFalse(); c.Configured.Should().BeFalse(); c.Physic.Should().Be("none"); },
            d => { d.Port.Should().Be("D"); d.LinkState.Should().BeFalse(); d.Configured.Should().BeFalse(); d.Physic.Should().Be("none"); });
    }

    [Fact]
    public void BuildPortInfo_marks_only_port_A_linked_for_the_last_slave_in_the_chain()
    {
        var ports = EtherCatClient.BuildPortInfo(new byte[4]);

        ports.Should().HaveCount(4);
        ports[0].LinkState.Should().BeTrue();
        ports.Skip(1).Should().OnlyContain(p => !p.LinkState && !p.Configured);
    }

    [Fact]
    public void BuildPortInfo_reports_a_healthy_zero_CRC_port_as_linked()
    {
        // Regression: link state used to be inferred from "crcCount > 0", which reported a
        // fault-free bus as fully unconnected and collapsed the topology into one trace per slave.
        var noErrors = new byte[8];

        var ports = EtherCatClient.BuildPortInfo(noErrors);

        ports[0].Physic.Should().Be("EBus");
        ports[0].LinkState.Should().BeTrue();
    }

    [Fact]
    public void BuildPortInfo_falls_back_to_unconfigured_ports_when_the_read_fails()
    {
        var ports = EtherCatClient.BuildPortInfo(null);

        ports.Should().HaveCount(4);
        ports.Should().OnlyContain(p => !p.Configured && !p.LinkState && p.Physic == "none");
    }
}
