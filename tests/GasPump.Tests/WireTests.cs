using System.Text.Json;
using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Security;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// What crosses the socket must come back the same. These are the records a
/// wrong attribute would break silently: the station would read nothing and
/// the fake-clock tests, which never serialize, would stay green.
/// </summary>
public class WireTests
{
    private static readonly ServiceStatus TwoParts = ServiceStatus.Of([DiagnosticCodes.PumpHoseBreakaway, DiagnosticCodes.PumpMeterNoPulse]);

    [Fact]
    public void A_service_report_with_two_codes_round_trips_as_a_sale_event_frame()
    {
        var frame = new LinkFrame(LinkFrame.TagEvent, E: new SaleEvent.PumpService("S7", TwoParts));
        var line = LinkJson.Encode(frame);
        Assert.Contains("PumpService", line);
        Assert.Contains("PUMP.HOSE.BREAKAWAY", line);

        var back = LinkJson.Decode(line)!;
        var e = Assert.IsType<SaleEvent.PumpService>(back.E);
        Assert.Equal("S7", e.SaleId);
        Assert.Equal(["PUMP.HOSE.BREAKAWAY", "PUMP.MTR.NOPULSE"], e.Service.Codes);
        Assert.False(e.Service.InService);
        Assert.Equal(TwoParts, e.Service);
    }

    [Fact]
    public void A_dispenser_view_round_trips_with_its_service_status_and_an_armed_authorization()
    {
        var armed = new ArmedAuthorization("S7", Money.FromDollars(30), Volume.FromGallons(50), TimeSpan.FromMinutes(10), true, "clamped to $500");
        var view = new DispenserView("PUMP-07", "DSP-1", 123, "Faulted", "[PUMP.HOSE.BREAKAWAY] x", "PUMP.HOSE.BREAKAWAY", 1234,
            false, false, false, Grade.Plus, true, true, true, false, armed, true, @"c:\x\chain.ndjson", TwoParts);
        var back = LinkJson.Decode(LinkJson.Encode(new LinkFrame(LinkFrame.TagView, V: view)))!;
        Assert.Equal(view, back.V);
        Assert.False(back.V!.Service.InService);
    }

    [Fact]
    public void Every_sale_event_and_pump_message_type_has_a_wire_name()
    {
        foreach (var t in typeof(SaleEvent).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(SaleEvent))))
            Assert.Contains(typeof(SaleEvent).GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false)
                .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>(), a => a.DerivedType == t);
        foreach (var t in typeof(PumpMessage).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(PumpMessage))))
            Assert.Contains(typeof(PumpMessage).GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false)
                .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>(), a => a.DerivedType == t);
    }

    [Fact]
    public void A_service_status_is_equal_by_its_codes_and_the_empty_one_is_in_service()
    {
        Assert.Equal(ServiceStatus.Of([DiagnosticCodes.TankFuelOut]), new ServiceStatus(["TANK.FUELOUT"]));
        Assert.NotEqual(TwoParts, ServiceStatus.Of([DiagnosticCodes.PumpMeterNoPulse, DiagnosticCodes.PumpHoseBreakaway]));
        Assert.True(ServiceStatus.Normal.InService);
        Assert.Equal(ServiceStatus.Normal, JsonSerializer.Deserialize<ServiceStatus>(JsonSerializer.Serialize(ServiceStatus.Normal, LinkJson.Options), LinkJson.Options));
    }
}
