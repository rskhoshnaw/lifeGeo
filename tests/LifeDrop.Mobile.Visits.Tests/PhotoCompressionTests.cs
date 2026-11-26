using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class PhotoCompressionTests
{
    [Fact]
    public void TheFinestStepWithinTheTargetIsKept_AndAnImpossiblePictureGetsTheSmallestResult()
    {
        var tried = new List<(int, float)>();
        // The picture is 900 KB at the first step and shrinks by a third each step.
        var size = 900 * 1024;
        var fits = PhotoCompression.Choose(step => { tried.Add(step); var b = new byte[size]; size = size * 2 / 3; return b; });
        Assert.Equal(3, tried.Count);                                   // 900 KB, 600 KB, 400 KB: the third fits, the finer ones did not
        Assert.Equal(PhotoCompression.Steps[2], tried[2]);
        Assert.True(fits.Length <= PhotoCompression.TargetBytes);

        var quick = PhotoCompression.Choose(step => new byte[10_000]);   // a simple picture keeps the finest quality
        Assert.Equal(10_000, quick.Length);
        tried.Clear();
        PhotoCompression.Choose(step => { tried.Add(step); return new byte[10_000]; });
        Assert.Single(tried);

        var big = PhotoCompression.Choose(step => new byte[5_000_000 - step.LongSide]);   // nothing fits: the smallest one made
        Assert.Equal(5_000_000 - 1600, big.Length);
    }
}
