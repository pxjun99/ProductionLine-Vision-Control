using OpenCvSharp;

namespace ProductionLine.UserApp;

/// <summary>카메라 장치는 한 작업 스레드에서만 열고 읽고 해제합니다.</summary>
internal interface ICameraFeed : IDisposable
{
    string? Error { get; }
    CameraFrame? Latest { get; }
    Task Completion { get; }
}

internal sealed class CameraSession : ICameraFeed
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private CameraFrame? _latest;
    private string? _error;
    private int _disposed;
    public string? Error { get { lock (_gate) return _error; } }
    public CameraFrame? Latest { get { lock (_gate) return _latest; } }
    public Task Completion { get; }

    public CameraSession(int index) => Completion = Task.Run(() => Capture(index));

    private void Capture(int index)
    {
        try
        {
            using var capture = new VideoCapture();
            var opened = capture.Open(index, VideoCaptureAPIs.DSHOW);
            if (!opened)
            {
                capture.Release();
                opened = capture.Open(index, VideoCaptureAPIs.MSMF);
            }
            if (!opened)
            {
                capture.Release();
                opened = capture.Open(index, VideoCaptureAPIs.ANY);
            }
            if (!opened)
                throw new IOException("카메라를 열 수 없습니다. 장치 번호·Windows 카메라 권한·다른 앱 사용 여부를 확인하세요.");
            capture.Set(VideoCaptureProperties.BufferSize, 1);
            using var frame = new Mat();
            long sequence = 0;
            var consecutiveFailures = 0;
            while (!_stop.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty())
                {
                    if (++consecutiveFailures >= 30)
                        throw new IOException("카메라 영상 수신이 끊겼습니다. 연결을 해제하고 다시 연결하세요.");
                    if (_stop.Token.WaitHandle.WaitOne(30)) break;
                    continue;
                }
                consecutiveFailures = 0;
                var jpeg = frame.ToBytes(".jpg");
                lock (_gate) _latest = new CameraFrame(jpeg, ++sequence, DateTime.UtcNow);
                if (_stop.Token.WaitHandle.WaitOne(30)) break;
            }
        }
        catch (Exception ex) { lock (_gate) { _error = ex.Message; _latest = null; } }
    }

    // Read가 끝난 뒤 작업 스레드에서 VideoCapture를 해제합니다.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _ = Completion.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}

internal sealed record CameraFrame(byte[] Jpeg, long Sequence, DateTime ReceivedUtc);
