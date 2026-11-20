using NotchTasks.Core.Shelf;
using Xunit;

namespace NotchTasks.Core.Tests.Shelf;

/// The shelf tiles' names: a long name keeps its start and its end around an ellipsis, in 14 characters.
public class ShelfTextTests
{
    [Fact]
    public void Middle_ShortUnchanged()
    {
        Assert.Equal("short.txt", ShelfText.Middle("short.txt"));
    }

    [Fact]
    public void Middle_ExactlyMax_Unchanged()
    {
        Assert.Equal("abcdefghijklmn", ShelfText.Middle("abcdefghijklmn"));
    }

    [Fact]
    public void Middle_Long_KeepsEnds()
    {
        // 25 characters: the first 6 and the last 7 are kept around the ellipsis, 14 in all.
        var shown = ShelfText.Middle("a-very-long-file-name.pdf");

        Assert.Equal("a-very…ame.pdf", shown);
        Assert.Equal(14, shown.Length);
    }

    [Fact]
    public void Middle_HonoursMax()
    {
        // max 10: (10 - 1) / 2 = 4 kept at the start, 10 - 1 - 4 = 5 at the end.
        Assert.Equal("abcd…lmnop", ShelfText.Middle("abcdefghijklmnop", 10));
    }
}
