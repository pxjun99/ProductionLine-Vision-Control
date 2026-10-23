using System.IO.Ports;
using System.Text.Json;

namespace ProductionLine.UserApp;

internal sealed class ArduinoSetupForm : Form
{
    private readonly ArduinoFirmwareTools _tools;
    private readonly DarkComboBox _ports = new() { Width = 120 };
    private readonly Button _refresh = new() { Text = "포트 확인", AutoSize = true };
    private readonly Button _prepare = new() { Text = "개발 도구 준비", AutoSize = true };
    private readonly Button _compile = new() { Text = "컴파일만 확인", AutoSize = true };
    private readonly Button _upload = new() { Text = "업로드·연결", AutoSize = true };
    private readonly Button _cancel = new() { Text = "작업 중단", AutoSize = true, Enabled = false };
    private readonly Button _close = new() { Text = "닫기", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, Text = "Mega 2560 · USB를 연결하고 COM 포트를 확인하세요.", AutoEllipsis = true };
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, Font = new Font("Consolas", 9), BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(13, 23, 34), ForeColor = IndustrialPalette.Text };
    private readonly DarkProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Blocks };
    private CancellationTokenSource? _operation;
    private bool _busy;
    public string? UploadedPort { get; private set; }

    public ArduinoSetupForm(string? preferredPort = null, ArduinoFirmwareTools? tools = null)
    {
        _tools = tools ?? new ArduinoFirmwareTools();
        Text = "아두이노 설정·업로드 · Mega 2560";
        Size = new Size(820, 610); MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Noto Sans KR", 10);
        BackColor = IndustrialPalette.Canvas; ForeColor = IndustrialPalette.Text;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6, BackColor = IndustrialPalette.Canvas };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "C#에서 아두이노 준비와 업로드를 진행합니다. Arduino IDE를 따로 열 필요가 없습니다.\r\nMega 2560 · PUL D5 / DIR D6 · 조명 DIN D7\r\nDHT11 D30 · MQ9 AO A0 · 분류 서보 D11 · NC 긴급 정지 D12–GND\r\n업로드하면 기존 보드 코드를 교체합니다. 벨트 정지·조명 켜짐으로 시작합니다.\r\n긴급 정지 접점이 열려 있으면 구동할 수 없습니다." }, 0, 0);
        var portRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        portRow.Controls.Add(new Label { Text = "Mega COM 포트", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        portRow.Controls.Add(_ports); portRow.Controls.Add(_refresh);
        layout.Controls.Add(portRow, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        actions.Controls.AddRange([_prepare, _compile, _upload, _cancel]);
        layout.Controls.Add(actions, 0, 2); layout.Controls.Add(_status, 0, 3); layout.Controls.Add(_log, 0, 4);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        bottom.Controls.Add(_progress, 0, 0); bottom.Controls.Add(_close, 1, 0);
        layout.Controls.Add(bottom, 0, 5); Controls.Add(layout);
        foreach (var button in new[] { _refresh, _prepare, _compile, _upload, _cancel, _close })
        {
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = IndustrialPalette.Border;
            button.BackColor = button == _upload ? Color.FromArgb(15, 124, 132) : IndustrialPalette.SurfaceRaised; button.ForeColor = IndustrialPalette.Text;
        }
        _ports.SelectedIndexChanged += (_, _) => UpdateControls();
        _refresh.Click += async (_, _) => await PerformAsync(async token => await ReloadPortsAsync(_ports.SelectedItem as string, token));
        _prepare.Click += async (_, _) => await PerformAsync(async token =>
        {
            await _tools.PrepareAsync(LogProgress(), token);
            _status.Text = "Mega 2560 개발 도구 준비 완료 · 컴파일 확인 또는 업로드·연결을 누르세요.";
            await ReloadPortsAsync(_ports.SelectedItem as string, token);
        });
        _compile.Click += async (_, _) => await PerformAsync(async token =>
        {
            await _tools.CompileAsync(LogProgress(), token);
            _status.Text = "Mega 2560 컴파일 성공 · 보드에 업로드하지 않았습니다.";
        });
        _upload.Click += async (_, _) =>
        {
            if (_ports.SelectedItem is not string port) return;
            await PerformAsync(async token =>
            {
                // Always compile the current bundled sketch before uploading.
                var build = await _tools.CompileAsync(LogProgress(), token);
                await _tools.UploadAsync(build, port, LogProgress(), token);
                ArduinoPortSettings.Save(port);
                UploadedPort = port;
                _status.Text = "업로드 성공 · 정지 상태 연결을 진행합니다.";
            });
            if (UploadedPort != null) { DialogResult = DialogResult.OK; Close(); }
        };
        _cancel.Click += (_, _) => _operation?.Cancel();
        _close.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; _operation?.Cancel(); _status.Text = "작업 중단을 기다리는 중입니다. 완료되면 닫기를 누르세요."; }
        };
        Shown += async (_, _) => await PerformAsync(async token => await ReloadPortsAsync(preferredPort ?? ArduinoPortSettings.Load(), token));
        UpdateControls();
    }

    private IProgress<string> LogProgress() => new Progress<string>(line =>
    {
        if (IsDisposed) return;
        if (_log.TextLength > 64000) _log.Clear();
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
    });

    private void UpdateControls()
    {
        _ports.Enabled = _refresh.Enabled = _prepare.Enabled = _compile.Enabled = !_busy;
        _upload.Enabled = !_busy && _ports.SelectedItem is string;
        _cancel.Enabled = _busy;
        _close.Enabled = !_busy;
        _progress.Style = _busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
    }

    private async Task PerformAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true; _operation = new CancellationTokenSource(); UpdateControls();
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { _status.Text = "작업 중단됨 · 업로드 중 중단했다면 다시 업로드하세요."; }
        catch (Exception ex) { _status.Text = "실패 · 아래 로그를 확인하세요."; LogProgress().Report(ex.Message); }
        finally { _busy = false; _operation.Dispose(); _operation = null; if (!IsDisposed) UpdateControls(); }
    }

    private async Task ReloadPortsAsync(string? preferred, CancellationToken token)
    {
        var ports = SerialPort.GetPortNames().OrderBy(value => value).ToArray();
        // 어두운 목록 상자 안의 ComboBox는 창에 붙어 있지 않아 DataSource가 채워지지 않습니다. Items에 직접 넣습니다.
        _ports.Items.Clear();
        _ports.Items.AddRange(ports.Cast<object>().ToArray());
        _ports.SelectedIndex = -1; // Never silently choose COM1 or an unrelated device.
        if (preferred != null && ports.Contains(preferred)) _ports.SelectedItem = preferred;
        if (_tools.IsPrepared)
        {
            var detected = await _tools.DiscoverAsync(token);
            var mega = detected.Where(value => value.Fqbn == ArduinoFirmwareTools.MegaFqbn && ports.Contains(value.Address)).ToArray();
            if (_ports.SelectedIndex < 0 && mega.Length == 1) _ports.SelectedItem = mega[0].Address;
            foreach (var value in detected) LogProgress().Report($"{value.Address}: {value.Name}");
        }
        if (ports.Length == 0) _status.Text = "COM 포트가 없습니다. Mega 2560의 USB 연결을 확인하세요.";
        else if (_ports.SelectedIndex < 0) _status.Text = "Mega 2560에 해당하는 COM 포트를 선택하세요. USB 변환칩 보드는 직접 선택할 수 있습니다.";
        UpdateControls();
    }
}

internal static class ArduinoPortSettings
{
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProductionLine", "ArduinoTools", "last-port.json");
    public static string? Load()
    {
        try { return JsonSerializer.Deserialize<PortSetting>(File.ReadAllText(PathName))?.Port; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static void Save(string port)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(new PortSetting(port)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Preferences must not turn a successful upload into failure. */ }
    }
    private sealed record PortSetting(string Port);
}
