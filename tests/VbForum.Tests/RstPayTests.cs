using VbForum.Data;

namespace VbForum.Tests;

public class RstPayTests
{
    [Fact]
    public void ParsesMembershipStatus()
    {
        var s = RstPayService.ParseStatus("OK\r\nstatus=active\nnextrebill=2026-12-01\ndaysuntilnextrebill=27\nrebillperioddays=30\nrebillamount=1990\ncurrency=EUR\nstartdate=2026-11-01\n");
        Assert.True(s.Ok);
        Assert.True(s.IsActive);
        Assert.Equal(new DateTime(2026, 12, 1), s.NextRebill);
        Assert.Equal(27, s.DaysUntilNextRebill);
        Assert.Equal(30, s.RebillPeriodDays);
        Assert.Equal(1990, s.RebillAmount);
        Assert.Equal("19,90 €", s.AmountText);
        Assert.Equal(new DateTime(2026, 11, 1), s.StartDate);
    }

    [Fact]
    public void ParsesMembershipStatusError()
    {
        var s = RstPayService.ParseStatus("ERROR: Membership not found");
        Assert.False(s.Ok);
        Assert.Equal("ERROR: Membership not found", s.Error);
    }
}
