namespace NotchTasks.Services;

/// One camera frame on its way to the UI thread: its size and its BGRA pixels (premultiplied, four bytes a pixel).
public sealed record MirrorPixels(int Width, int Height, byte[] Bgra);

/// The frames waiting for the UI thread. A camera thread offers each frame; at most one frame waits, and a newer frame replaces
/// a waiting one, so the UI never falls behind the camera. The UI thread takes the waiting frame, shows it, and returns its buffer.
/// Offer copies the pixels, so the camera may reuse its own buffer at once. Buffers are reused, so the frames cost no new memory
/// at a steady size.
public sealed class MirrorFrames
{
    private readonly object _gate = new();
    private MirrorPixels? _pending;   // the frame waiting for the UI thread
    private MirrorPixels? _spare;     // a buffer free for the next frame
    private bool _scheduled;          // a delivery to the UI thread is already queued

    /// Stores a frame, copying its pixels. Returns true when the caller must queue a delivery to the UI thread; false when one is
    /// already queued (it will take this frame).
    public bool Offer(int width, int height, byte[] bgra)
    {
        var length = checked(width * height * 4);
        lock (_gate)
        {
            var buffer = _spare is { } spare && spare.Bgra.Length == length && spare.Width == width && spare.Height == height
                ? spare
                : new MirrorPixels(width, height, new byte[length]);
            _spare = null;
            Buffer.BlockCopy(bgra, 0, buffer.Bgra, 0, length);
            if (_pending is { } replaced)
            {
                _spare = replaced;  // the frame it replaces is not shown: its buffer is kept
            }
            _pending = buffer;
            var schedule = !_scheduled;
            _scheduled = true;
            return schedule;
        }
    }

    /// Takes the waiting frame for the UI thread, or null when none waits. The caller returns it with Return once shown.
    public MirrorPixels? Take()
    {
        lock (_gate)
        {
            var pending = _pending;
            _pending = null;
            _scheduled = false;
            return pending;
        }
    }

    /// Gives back a taken frame's buffer, for the next frame to reuse.
    public void Return(MirrorPixels frame)
    {
        lock (_gate)
        {
            _spare = frame;
        }
    }

    /// Drops the waiting frame (the mirror closed).
    public void Clear()
    {
        lock (_gate)
        {
            if (_pending is { } pending)
            {
                _spare = pending;
            }
            _pending = null;
        }
    }
}
