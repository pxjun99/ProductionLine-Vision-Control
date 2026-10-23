using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProductionLine.UserApp;

internal sealed record ArduinoCommandResult(int ExitCode, string Output, string Error);
internal interface IArduinoCommandRunner
{
    Task<ArduinoCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string directory, TimeSpan timeout, IProgress<string>? progress, CancellationToken cancellation);
}

internal sealed class ArduinoProcessRunner : IArduinoCommandRunner
{
    public async Task<ArduinoCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string directory, TimeSpan timeout, IProgress<string>? progress, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var value in arguments) info.ArgumentList.Add(value);
        // Use our isolated configuration and avoid forwarding AI credentials to subprocesses.
        foreach (var key in info.Environment.Keys.ToArray())
            if (key.StartsWith("ARDUINO_", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase)) info.Environment.Remove(key);
        using var process = new Process { StartInfo = info };
        cancellation.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("아두이노 도구를 실행하지 못했습니다.");
        async Task<string> DrainAsync(StreamReader reader)
        {
            var output = new StringBuilder();
            while (await reader.ReadLineAsync() is string line)
            {
                if (output.Length < 128 * 1024) output.AppendLine(line);
                progress?.Report(line);
            }
            return output.ToString();
        }
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(timeout);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            throw new TimeoutException("아두이노 작업 제한 시간이 지났습니다. 로그를 확인하고 다시 시도하세요.");
        }
        return new(process.ExitCode, await stdout, await stderr);
    }
}

internal sealed record ArduinoDetectedPort(string Address, string Name, string? Fqbn);
internal sealed record ArduinoFirmwareBuild(string SketchDirectory, string BuildDirectory, string HexPath, string HexSha256);

internal sealed class ArduinoFirmwareTools
{
    public const string MegaFqbn = "arduino:avr:mega:cpu=atmega2560";
    public const string CoreVersion = "1.8.8";
    private const string CliSha256 = "1017DE89179C3167E6B8A38CA6CC4091FA69A3CF97AAFA7810F01423003F5571";
    private readonly IArduinoCommandRunner _runner;
    private readonly string _cli, _firmware;
    public string Root { get; }
    private string Config => Path.Combine(Root, "arduino-cli.yaml");
    public bool IsPrepared => File.Exists(Path.Combine(Root, "data", "packages", "arduino", "hardware", "avr", CoreVersion, "platform.txt"));

    public ArduinoFirmwareTools(string? baseDirectory = null, string? root = null, IArduinoCommandRunner? runner = null)
    {
        var basis = baseDirectory ?? AppContext.BaseDirectory;
        _cli = Path.Combine(basis, "Tools", "arduino-cli", "arduino-cli.exe");
        _firmware = Path.Combine(basis, "Hardware", "ConveyorYoloControl", "ConveyorYoloControl.ino");
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProductionLine", "ArduinoTools"));
        _runner = runner ?? new ArduinoProcessRunner();
    }

    private FileStream BeginOperation()
    {
        if (!File.Exists(_cli)) throw new FileNotFoundException("아두이노 업로드 도구가 없습니다. 사용자 앱을 다시 빌드하세요.", _cli);
        if (!File.Exists(_firmware)) throw new FileNotFoundException("컨베이어 아두이노 코드가 없습니다. 사용자 앱을 다시 빌드하세요.", _firmware);
        if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_cli))).Equals(CliSha256, StringComparison.Ordinal))
            throw new IOException("아두이노 도구 파일이 배포본과 다릅니다. 공식 포함 파일로 복구하세요.");
        Directory.CreateDirectory(Root);
        FileStream operation;
        try { operation = new FileStream(Path.Combine(Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("다른 창에서 아두이노 준비·업로드 중입니다. 작업이 끝난 뒤 다시 시도하세요."); }
        try
        {
            var configuration = new
            {
                directories = new { data = Path.Combine(Root, "data"), downloads = Path.Combine(Root, "downloads"), user = Path.Combine(Root, "sketchbook") },
                build_cache = new { path = Path.Combine(Root, "cache") },
                metrics = new { enabled = false }, updater = new { enable_notification = false },
                network = new { connection_timeout = "60s", cloud_api = new { skip_board_detection_calls = true } },
                locale = "en", output = new { no_color = true }
            };
            // JSON is valid YAML; paths are escaped by the serializer.
            File.WriteAllText(Config, JsonSerializer.Serialize(configuration), new UTF8Encoding(false));
            return operation;
        }
        catch { operation.Dispose(); throw; }
    }

    private async Task<string> RunAsync(string[] command, TimeSpan timeout, IProgress<string>? progress, CancellationToken cancellation)
    {
        var arguments = new List<string> { "--config-file", Config, "--no-color" };
        arguments.AddRange(command);
        var result = await _runner.RunAsync(_cli, arguments, Root, timeout, progress, cancellation);
        if (result.ExitCode != 0)
        {
            var detail = (result.Error + "\n" + result.Output).Trim();
            if (detail.Length > 1400) detail = detail[^1400..];
            throw new IOException($"아두이노 {command[0]} 작업 실패 (코드 {result.ExitCode})\n{detail}");
        }
        return result.Output;
    }

    private async Task PrepareCoreAsync(IProgress<string>? progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (IsPrepared) { progress?.Report("Mega 2560 개발 도구 준비됨 · 설치를 재사용합니다."); return; }
        progress?.Report("처음 준비: Mega 2560 개발 도구 다운로드 중 · 인터넷 연결이 필요합니다.");
        await RunAsync(["core", "update-index"], TimeSpan.FromMinutes(4), progress, cancellation);
        await RunAsync(["core", "install", "arduino:avr@" + CoreVersion], TimeSpan.FromMinutes(8), progress, cancellation);
        if (!IsPrepared) throw new IOException("Mega 2560 개발 도구 설치를 확인하지 못했습니다.");
    }

    public async Task PrepareAsync(IProgress<string>? progress, CancellationToken cancellation)
    {
        using var operation = BeginOperation();
        await PrepareCoreAsync(progress, cancellation);
    }

    public async Task<IReadOnlyList<ArduinoDetectedPort>> DiscoverAsync(CancellationToken cancellation)
    {
        using var operation = BeginOperation();
        if (!IsPrepared) return [];
        var json = await RunAsync(["board", "list", "--json", "--discovery-timeout", "2s"], TimeSpan.FromSeconds(20), null, cancellation);
        return ParsePorts(json);
    }

    internal static IReadOnlyList<ArduinoDetectedPort> ParsePorts(string json)
    {
        using var document = JsonDocument.Parse(json);
        var ports = new List<ArduinoDetectedPort>();
        if (!document.RootElement.TryGetProperty("detected_ports", out var detected)) return ports;
        foreach (var entry in detected.EnumerateArray())
        {
            if (!entry.TryGetProperty("port", out var port) || !port.TryGetProperty("address", out var address)) continue;
            var value = address.GetString() ?? "";
            if (!Regex.IsMatch(value, @"^COM[1-9][0-9]{0,3}$", RegexOptions.IgnoreCase)) continue;
            string? fqbn = null;
            var name = "보드 자동 식별 안 됨";
            if (entry.TryGetProperty("matching_boards", out var boards))
                foreach (var board in boards.EnumerateArray())
                    if (board.TryGetProperty("fqbn", out var id) && (id.GetString() ?? "").StartsWith("arduino:avr:mega", StringComparison.Ordinal))
                    {
                        fqbn = MegaFqbn;
                        name = board.TryGetProperty("name", out var title) ? title.GetString() ?? "Mega 2560" : "Mega 2560";
                        break;
                    }
            ports.Add(new(value.ToUpperInvariant(), name, fqbn));
        }
        return ports;
    }

    public async Task<ArduinoFirmwareBuild> CompileAsync(IProgress<string>? progress, CancellationToken cancellation)
    {
        using var operation = BeginOperation();
        await PrepareCoreAsync(progress, cancellation);
        var job = Path.Combine(Root, "jobs", Guid.NewGuid().ToString("N"));
        var sketch = Path.Combine(job, "ConveyorYoloControl");
        var output = Path.Combine(job, "build");
        Directory.CreateDirectory(sketch);
        Directory.CreateDirectory(output);
        var firmwareRoot = Path.GetDirectoryName(_firmware)!;
        foreach (var file in Directory.EnumerateFiles(firmwareRoot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(sketch, Path.GetRelativePath(firmwareRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        progress?.Report("Mega 2560용 컨베이어 코드 컴파일 중...");
        await RunAsync(["compile", "--fqbn", MegaFqbn, "--output-dir", output, "--jobs", "2", sketch], TimeSpan.FromMinutes(3), progress, cancellation);
        var hex = Path.Combine(output, "ConveyorYoloControl.ino.hex");
        if (!File.Exists(hex)) throw new IOException("컴파일 결과 파일을 찾지 못했습니다. 업로드하지 않습니다.");
        progress?.Report("컴파일 성공 · V7 · 제품 인식 후 2배 가속 (유지 시간 0.5~60초 설정) · 분류 이동·유지 각각 최대 60초 · PUL D5 / DIR D6 · 반주기 100µs · 조명 D7 · DHT11 D30 / MQ9 AO A0 / 서보 D11 / NC 긴급 정지 D12 · 업로드 후 벨트 정지");
        return new(sketch, output, hex, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hex))));
    }

    public async Task UploadAsync(ArduinoFirmwareBuild build, string port, IProgress<string>? progress, CancellationToken cancellation)
    {
        if (!Regex.IsMatch(port, @"^COM[1-9][0-9]{0,3}$", RegexOptions.IgnoreCase)) throw new ArgumentException("올바른 COM 포트를 선택하세요.");
        using var operation = BeginOperation();
        var jobs = Path.GetFullPath(Path.Combine(Root, "jobs")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(build.HexPath).StartsWith(jobs, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(build.BuildDirectory).StartsWith(jobs, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(build.SketchDirectory).StartsWith(jobs, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(build.HexPath) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(build.HexPath))) != build.HexSha256)
            throw new IOException("컴파일 결과가 바뀌었거나 유효하지 않습니다. 다시 컴파일하세요.");
        progress?.Report($"{port}에 컨베이어 코드 업로드 중...");
        await RunAsync(["upload", "--fqbn", MegaFqbn, "--port", port.ToUpperInvariant(), "--input-dir", build.BuildDirectory, "--verify", build.SketchDirectory], TimeSpan.FromSeconds(90), progress, cancellation);
        progress?.Report("업로드 성공 · 창을 닫고 펌웨어 응답과 정지 상태를 확인합니다.");
    }
}
