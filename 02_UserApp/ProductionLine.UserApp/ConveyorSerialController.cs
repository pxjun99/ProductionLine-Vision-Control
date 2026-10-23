using System.Diagnostics;
using System.IO.Ports;

namespace ProductionLine.UserApp;

internal interface IConveyorController : IDisposable
{
    bool IsRunning { get; }
    Task RunAsync();
    Task StopAsync();
    Task PingAsync();
}

internal interface IConveyorTransport : IDisposable
{
    void WriteLine(string value);
    string ReadLine();
}

internal interface IConveyorLightingController
{
    bool? IsLightOn { get; }
    Task SetLightAsync(bool enabled);
    Task RefreshLightAsync();
}

internal interface IHardwareConveyorController
{
    bool SupportsHardware { get; }
    Task<ProductionLine.Shared.UpdateLineHardwareRequest> ReadHardwareAsync();
    Task QueueSortAsync(Guid productId, DateTimeOffset dueAt, int holdMs);
    Task ResetEmergencyAsync();
}

/// <summary>모든 명령은 아두이노의 응답을 확인한 후에만 성공 처리합니다.</summary>
internal sealed class ConveyorSerialController(IConveyorTransport transport) : IConveyorController, IConveyorLightingController, IHardwareConveyorController, IRecognitionConveyorController
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private bool _disposed;
    public bool IsRunning { get; private set; }
    private bool _supportsLongSorting;
    public bool? IsLightOn { get; private set; }
    /// <summary>긴급 정지 버튼이 원위치돼 잠금 해제를 받을 수 있는지. 이 값을 보내지 않는 옛 펌웨어면 null.</summary>
    public bool? EmergencySwitchReady { get; private set; }
    public bool SupportsHardware { get; private set; }
    public bool SupportsRecognitionBoost { get; private set; }
    public bool SupportsConfigurableBoost { get; private set; }

    public static string[] GetPortNames() => SerialPort.GetPortNames().OrderBy(value => value).ToArray();

    public static async Task<ConveyorSerialController> ConnectAsync(string portName, bool stopMotorOnConnect = true)
    {
        var serial = new SerialPort(portName, 9600)
        {
            NewLine = "\n", ReadTimeout = 150, WriteTimeout = 500,
            DtrEnable = true, RtsEnable = false
        };
        ConveyorSerialController? controller = null;
        try
        {
            await Task.Run(serial.Open);
            // 아두이노는 USB 시리얼 연결 시 리셋될 수 있으므로 부팅을 기다립니다.
            await Task.Delay(2000);
            controller = new ConveyorSerialController(new SerialTransport(serial));
            await controller.InitializeAsync(stopMotorOnConnect);
            // Keep older firmware compatible; modern hardware lights up during initialization.
            try { await controller.RefreshLightAsync(); }
            catch (Exception ex) when (ex is IOException or TimeoutException) { }
            return controller;
        }
        catch
        {
            if (controller != null) controller.Dispose();
            else serial.Dispose();
            throw;
        }
    }

    internal async Task InitializeAsync(bool stopMotorOnConnect)
    {
        await HandshakeAsync();
        if (stopMotorOnConnect) await StopAsync();
        // 사용자앱 v6.1: 센서 지원(V4 이상) 장치는 연결 시 조명을 켜고 켜짐 응답을 확인합니다.
        if (SupportsHardware) await SetLightAsync(true);
    }

    internal Task HandshakeAsync() => SendAsync("HELLO", value => value is
        "PL_CONVEYOR_PUL_DIR_V3 STOPPED" or "PL_CONVEYOR_PUL_DIR_V3 RUNNING" or
        "PL_CONVEYOR_PUL_DIR_V4 STOPPED" or "PL_CONVEYOR_PUL_DIR_V4 RUNNING" or
        "PL_CONVEYOR_PUL_DIR_V5 STOPPED" or "PL_CONVEYOR_PUL_DIR_V5 RUNNING" or
        "PL_CONVEYOR_PUL_DIR_V6 STOPPED" or "PL_CONVEYOR_PUL_DIR_V6 RUNNING" or
        "PL_CONVEYOR_PUL_DIR_V7 STOPPED" or "PL_CONVEYOR_PUL_DIR_V7 RUNNING");
    public Task RunAsync() => SendAsync("RUN", value => value == "RUNNING");
    public Task StopAsync() => SendAsync("STOP", value => value == "STOPPED");
    public Task PingAsync() => SendAsync("PING", value => value is "RUNNING" or "STOPPED");
    public Task SetLightAsync(bool enabled) => SendAsync(enabled ? "LED_ON" : "LED_OFF",
        value => value == (enabled ? "LED_ON" : "LED_OFF"));
    public Task RefreshLightAsync() => SendAsync("LED_STATUS", value => value is "LED_ON" or "LED_OFF");
    public async Task<ProductionLine.Shared.UpdateLineHardwareRequest> ReadHardwareAsync()
    {
        if (!SupportsHardware) throw new InvalidOperationException("센서·분류 V4 펌웨어 업로드가 필요합니다.");
        ProductionLine.Shared.UpdateLineHardwareRequest? status = null;
        await SendAsync("STATUS", reply =>
        {
            if (!reply.StartsWith("HW ", StringComparison.Ordinal)) return false;
            status = HardwareSerialProtocol.Parse(reply);
            EmergencySwitchReady = reply.Contains("\"emergencySwitchReady\":true") ? true
                : reply.Contains("\"emergencySwitchReady\":false") ? false : null;
            return true;
        });
        IsRunning = status!.BeltRunning;
        return status;
    }
    public Task QueueSortAsync(Guid productId, DateTimeOffset dueAt, int holdMs)
    {
        if (!SupportsHardware || productId == Guid.Empty || holdMs is < 200 or > 60000)
            throw new InvalidOperationException("분류 펌웨어와 이동·통과 시간을 확인하세요.");
        var id = productId.ToString("N");
        return SendAsync("SORT", reply => reply == "SORT_QUEUED " + id,
            () =>
            {
                if (!_supportsLongSorting && (holdMs > 5000 || (dueAt-DateTimeOffset.UtcNow).TotalMilliseconds > 10080))
                    throw new InvalidOperationException("이동 10초·유지 5초를 초과하려면 아두이노 설정·업로드에서 V5 코드를 업로드하세요.");
                return HardwareSerialProtocol.CreateSortCommand(productId, dueAt, holdMs, DateTimeOffset.UtcNow);
            });
    }
    public Task ResetEmergencyAsync() => SendAsync("RESET_ESTOP", reply => reply == "ESTOP_RESET");
    /// <summary>YOLO 판정 표시등: 불량이면 빨강(D40), 정상이면 초록(D42). 이 명령이 없는 옛 펌웨어면 다음부터 보내지 않습니다.</summary>
    public bool ResultLampUnsupported { get; private set; }
    public async Task SetResultLampAsync(bool defect)
    {
        if (ResultLampUnsupported) return;
        var command = defect ? "RESULT 1" : "RESULT 0";
        try { await SendAsync(command, reply => reply == command); }
        catch (IOException ex) when (ex.Message.Contains("UNKNOWN_COMMAND")) { ResultLampUnsupported = true; }
    }
    public Task RecognizeAsync(Guid productId, DateTime capturedUtc, int travelMs, int holdMs, bool sort, int boostMs = 3000)
    {
        if (!SupportsRecognitionBoost) throw new InvalidOperationException("인식 후 가속에는 V6 코드 업로드가 필요합니다.");
        if (boostMs is < 500 or > 60000) throw new ArgumentOutOfRangeException(nameof(boostMs));
        if (!SupportsConfigurableBoost && boostMs != 3000) throw new InvalidOperationException("가속 시간 변경에는 아두이노 설정·업로드에서 V7 코드를 올리세요.");
        return SendAsync("PRODUCT", reply => reply == $"PRODUCT_OK {productId:N}",
            () => HardwareSerialProtocol.CreateProductCommand(productId,capturedUtc,travelMs,holdMs,sort,DateTimeOffset.UtcNow,SupportsConfigurableBoost ? boostMs : null));
    }

    private async Task SendAsync(string command, Func<string, bool> accepts, Func<string>? prepareCommand = null)
    {
        await _commands.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var sent = prepareCommand?.Invoke() ?? command;
                    transport.WriteLine(sent);
                    var timeout = Stopwatch.StartNew();
                    while (timeout.ElapsedMilliseconds < 1200)
                    {
                        string reply;
                        try { reply = transport.ReadLine().Trim(); }
                        catch (TimeoutException) { continue; }
                        if (reply.StartsWith("ERROR"))
                            throw new IOException($"아두이노 {command} 명령 오류: {reply} · 전송=[{sent}]");
                        if (command != "HELLO" && reply.StartsWith("PL_CONVEYOR_"))
                            throw new IOException($"아두이노 재시작 또는 통신 재동기화 필요 · 요청={command} · 수신={reply} · 전원과 연결을 확인한 뒤 다시 연결하세요.");
                        if (command == "HELLO" && reply.StartsWith("PL_CONVEYOR_") &&
                            !reply.StartsWith("PL_CONVEYOR_PUL_DIR_V3 ") && !reply.StartsWith("PL_CONVEYOR_PUL_DIR_V4 ") && !reply.StartsWith("PL_CONVEYOR_PUL_DIR_V5 ") && !reply.StartsWith("PL_CONVEYOR_PUL_DIR_V6 ") && !reply.StartsWith("PL_CONVEYOR_PUL_DIR_V7 "))
                            throw new IOException("이전 핀 설정의 펌웨어입니다. 아두이노 설정·업로드에서 PUL 5번 / DIR 6번 / 100µs 코드를 다시 업로드하세요. 수신: " + reply);
                        if (!accepts(reply)) continue;
                        if (command == "HELLO")
                        {
                            SupportsConfigurableBoost = reply.StartsWith("PL_CONVEYOR_PUL_DIR_V7 ");
                            SupportsRecognitionBoost = SupportsConfigurableBoost || reply.StartsWith("PL_CONVEYOR_PUL_DIR_V6 ");
                            _supportsLongSorting = SupportsRecognitionBoost || reply.StartsWith("PL_CONVEYOR_PUL_DIR_V5 ");
                            SupportsHardware = _supportsLongSorting || reply.StartsWith("PL_CONVEYOR_PUL_DIR_V4 ");
                        }
                        if (reply is "LED_ON" or "LED_OFF") IsLightOn = reply == "LED_ON";
                        else if (reply is "RUNNING" or "STOPPED" || command == "HELLO")
                            IsRunning = reply == "RUNNING" || reply.EndsWith(" RUNNING");
                        return;
                    }
                    throw new TimeoutException($"아두이노 {command} 응답을 확인하지 못했습니다. 아두이노 설정·업로드에서 컨베이어 코드를 업로드했는지 확인하세요.");
                }
            });
        }
        finally { _commands.Release(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // 종료 시 정지를 요청합니다. 전달되지 않아도 펌웨어의 통신 타임아웃이 정지시킵니다.
            try { transport.WriteLine("STOP"); } catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { }
            transport.Dispose();
        }
    }

    private sealed class SerialTransport(SerialPort serial) : IConveyorTransport
    {
        public void WriteLine(string value) => serial.WriteLine(value);
        public string ReadLine() => serial.ReadLine();
        public void Dispose() => serial.Dispose();
    }
}
