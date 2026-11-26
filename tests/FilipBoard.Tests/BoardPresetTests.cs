using FilipBoard.Core;
using Xunit;

namespace FilipBoard.Tests;

public class BoardPresetTests
{
    [Fact]
    public void BiosReplica_curves_are_valid_and_leave_other_channels_on_bios()
    {
        var p = BoardPresets.BiosReplica();
        Assert.Equal(FanMode.Auto, p.Default.Mode);
        Assert.Equal(3, p.Channels.Count);
        foreach (var (id, cfg) in p.Channels)
        {
            Assert.Empty(cfg.Curve.Validate(cfg.MinSafePercent));
            Assert.False(cfg.AllowFanStop);
            Assert.Contains(id, BoardPresets.Labels.Keys);
        }
    }

    [Fact]
    public void Labels_only_apply_to_the_target_board()
    {
        var id = "/lpc/it8686e/0/control/0";
        Assert.NotNull(BoardPresets.LabelFor(BoardPresets.Fingerprint, id));
        Assert.Null(BoardPresets.LabelFor("Other board|chip", id));
    }
}
