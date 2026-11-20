using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace NotchTasks.Services;

/// How a camera session started (the mirror card's messages).
public enum CameraStart
{
    /// The camera runs and frames arrive.
    Running,
    /// Windows does not let the app use the camera (the privacy setting).
    Denied,
    /// This PC has no video capture device.
    NoCamera,
    /// The camera could not be opened or read.
    Failed,
}

/// A live camera for the mirror card. Start opens it and calls frame with each frame; Stop releases it. Frames may arrive on any
/// thread, and the bgra array is valid only during the call: the frame is copied out by the caller.
public interface ICameraSession : IDisposable
{
    /// Opens the camera and returns how it went. A session starts once.
    Task<CameraStart> Start(Action<int, int, byte[]> frame);

    /// Stops the frames and releases the camera. Safe to call more than once, and before Start has finished.
    Task Stop();
}

/// The Windows camera (Mac AVCaptureSession): the first video capture device, opened with exclusive control at the smallest format
/// at least 640 px wide and at most 30 fps. If another app holds the camera, it opens shared and read-only instead. Its frames are
/// BGRA, premultiplied, read in realtime mode. The capture and its reader are released off the UI thread.
public sealed class CameraSession : ICameraSession
{
    private const int MinWidth = 640;
    private const double MaxFrameRate = 30;

    private readonly object _gate = new();
    private readonly object _pixelsGate = new();
    private bool _started;
    private bool _stopped;
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private Action<int, int, byte[]>? _frame;
    private byte[]? _pixels;  // reused while the frame size stays the same

    public async Task<CameraStart> Start(Action<int, int, byte[]> frame)
    {
        lock (_gate)
        {
            if (_started)
            {
                throw new InvalidOperationException("The camera session is already started.");
            }
            _started = true;
            if (_stopped)
            {
                return CameraStart.Failed;
            }
            _frame = frame;
        }
        MediaCapture? capture = null;
        MediaFrameReader? reader = null;
        try
        {
            var device = (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).FirstOrDefault();
            if (device is null)
            {
                return CameraStart.NoCamera;
            }
            (capture, var exclusive) = await InitializeAsync(device.Id);
            var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (source is null)
            {
                await ReleaseAsync(capture, null);
                return CameraStart.Failed;
            }
            if (exclusive)
            {
                await ChooseFormatAsync(source);
            }
            reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success)
            {
                await ReleaseAsync(capture, reader);
                return CameraStart.Failed;
            }

            lock (_gate)
            {
                if (!_stopped)
                {
                    _capture = capture;
                    _reader = reader;
                    return CameraStart.Running;
                }
            }
            // Stop ran while the camera was starting: it is released at once, and nothing is kept.
            await ReleaseAsync(capture, reader);
            return CameraStart.Failed;
        }
        catch (UnauthorizedAccessException)
        {
            await ReleaseAsync(capture, reader);
            return CameraStart.Denied;
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
            await ReleaseAsync(capture, reader);
            return CameraStart.Failed;
        }
    }

    public Task Stop()
    {
        MediaCapture? capture;
        MediaFrameReader? reader;
        lock (_gate)
        {
            _stopped = true;
            _frame = null;
            capture = _capture;
            reader = _reader;
            _capture = null;
            _reader = null;
        }
        return ReleaseAsync(capture, reader);
    }

    public void Dispose() => _ = Stop();

    /// Opens the device with exclusive control. A failure (the camera is in use, typically) retries once with shared read-only access.
    /// The UnauthorizedAccessException of a denied camera is not retried. The bool says whether the capture is exclusive.
    private static async Task<(MediaCapture Capture, bool Exclusive)> InitializeAsync(string deviceId)
    {
        var settings = new MediaCaptureInitializationSettings
        {
            VideoDeviceId = deviceId,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        };
        var exclusive = new MediaCapture();
        try
        {
            await exclusive.InitializeAsync(settings);
            return (exclusive, true);
        }
        catch (UnauthorizedAccessException)
        {
            exclusive.Dispose();
            throw;
        }
        catch (Exception)
        {
            exclusive.Dispose();
        }

        settings.SharingMode = MediaCaptureSharingMode.SharedReadOnly;
        var shared = new MediaCapture();
        try
        {
            await shared.InitializeAsync(settings);
            return (shared, false);
        }
        catch
        {
            shared.Dispose();
            throw;
        }
    }

    /// The smallest format at least 640 px wide and at most 30 fps. A camera with none keeps its own format.
    private static async Task ChooseFormatAsync(MediaFrameSource source)
    {
        var format = source.SupportedFormats
            .Where(f => f.VideoFormat.Width >= MinWidth && FrameRate(f) <= MaxFrameRate)
            .OrderBy(f => f.VideoFormat.Width)
            .ThenBy(f => f.VideoFormat.Height)
            .FirstOrDefault();
        if (format is not null)
        {
            await source.SetFormatAsync(format);
        }
    }

    private static double FrameRate(MediaFrameFormat format) =>
        format.FrameRate.Denominator == 0 ? 0 : format.FrameRate.Numerator / (double)format.FrameRate.Denominator;

    /// Copies each frame's pixels into the reused buffer and hands it to the frame callback.
    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        Action<int, int, byte[]>? frame;
        lock (_gate)
        {
            frame = _frame;
        }
        if (frame is null)
        {
            return;
        }
        using var mediaFrame = sender.TryAcquireLatestFrame();
        var bitmap = mediaFrame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
        {
            return;
        }
        using var bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var length = checked(width * height * 4);
        lock (_pixelsGate)
        {
            if (_pixels is null || _pixels.Length != length)
            {
                _pixels = new byte[length];
            }
            bgra.CopyToBuffer(_pixels.AsBuffer());
            frame(width, height, _pixels);
        }
    }

    /// Releases a reader and a capture off the UI thread. Errors are logged: a release never throws.
    private Task ReleaseAsync(MediaCapture? capture, MediaFrameReader? reader) => Task.Run(async () =>
    {
        try
        {
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                await reader.StopAsync();
                reader.Dispose();
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        try
        {
            capture?.Dispose();
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
    });
}
