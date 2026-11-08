using HadramoutNET.Core.Visual;
using Xunit;

namespace HadramoutNET.Tests;

public sealed class RedPaletteTests
{
    [Fact]
    public void Selected_main_colors_are_red_not_navy()
    {
        Assert.Equal("#7C0F24", RedPalette.Dark);
        Assert.Equal("#BB1838", RedPalette.Primary);
        Assert.Equal("#E53D58", RedPalette.Shine);
        Assert.Equal("#FFFFFF", RedPalette.Surface);
    }
}
