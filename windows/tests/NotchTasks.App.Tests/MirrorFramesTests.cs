using System.Linq;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The mirror's frame slot: a newer frame replaces the waiting one, one delivery is queued at a time, and a buffer still held by
/// the UI is never written again. Pure logic: no WPF, no camera.
public sealed class MirrorFramesTests
{
    /// A frame of 2 by 2 pixels (16 bytes) filled with one value.
    private static byte[] Frame(byte value) => Enumerable.Repeat(value, 16).ToArray();

    [Fact]
    public void NewerFrame_ReplacesThePendingOne()
    {
        var frames = new MirrorFrames();
        frames.Offer(2, 2, Frame(1));
        frames.Offer(2, 2, Frame(2));

        var taken = frames.Take();

        Assert.NotNull(taken);
        Assert.Equal(Frame(2), taken!.Bgra);
        Assert.Null(frames.Take());  // the replaced frame is gone
    }

    [Fact]
    public void Offer_ReturnsTrueOnlyOncePerPendingDelivery()
    {
        var frames = new MirrorFrames();

        Assert.True(frames.Offer(2, 2, Frame(1)));   // queues the delivery
        Assert.False(frames.Offer(2, 2, Frame(2)));  // one is queued already: this frame waits in its place
        Assert.False(frames.Offer(2, 2, Frame(3)));

        frames.Take();  // the delivery runs and takes the frame
        Assert.True(frames.Offer(2, 2, Frame(4)));  // the next frame queues a delivery again
    }

    [Fact]
    public void SpareBuffer_NeverAliasesAFrameStillHeld()
    {
        var frames = new MirrorFrames();
        frames.Offer(2, 2, Frame(1));
        var held = frames.Take()!;  // the UI holds this frame: it is not returned yet

        frames.Offer(2, 2, Frame(2));  // must not write into the held buffer
        var second = frames.Take()!;
        frames.Offer(2, 2, Frame(3));  // second is still held as well

        Assert.NotSame(held.Bgra, second.Bgra);
        Assert.Equal(Frame(1), held.Bgra);
        Assert.Equal(Frame(2), second.Bgra);
    }

    [Fact]
    public void ReturnedBuffer_IsReusedForTheNextFrame()
    {
        var frames = new MirrorFrames();
        frames.Offer(2, 2, Frame(1));
        var first = frames.Take()!;
        frames.Return(first);

        frames.Offer(2, 2, Frame(2));
        var second = frames.Take()!;

        Assert.Same(first.Bgra, second.Bgra);
        Assert.Equal(Frame(2), second.Bgra);
    }
}
