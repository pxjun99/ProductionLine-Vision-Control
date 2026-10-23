using System.Text.Json;
using ProductionLine.Shared;

namespace ProductionLine.UserApp;

internal static class HardwareSerialProtocol
{
    public static string CreateSortCommand(Guid id, DateTimeOffset dueAt, int holdMs, DateTimeOffset now)
    {
        // About 80 ms to transmit a SORT command at 9600 baud. Recompute after
        // acquiring the serial queue so model inference/status polling time is counted.
        var remaining = Math.Floor((dueAt - now).TotalMilliseconds) - 80;
        if (id == Guid.Empty || remaining < 50 || remaining > 60000 || holdMs is < 200 or > 60000)
            throw new InvalidOperationException("분류 시점이 지났거나 너무 가깝습니다. 벨트를 정지하고 제품 위치를 확인하세요.");
        return $"SORT {id:N} {(int)remaining} {holdMs}";
    }

    public static UpdateLineHardwareRequest Parse(string reply)
    {
        if (!reply.StartsWith("HW ")) throw new IOException("장비 상태 응답 형식이 잘못됐습니다.");
        using var document = JsonDocument.Parse(reply[3..]);
        var root = document.RootElement;
        bool Flag(string name) => root.GetProperty(name).GetBoolean();
        double? Number(string name)
        {
            var value = root.GetProperty(name);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
        }
        var temperature = Number("temperatureCelsius");
        var humidity = Number("humidityPercent");
        var gas = root.GetProperty("gasDetected");
        var stopped = Flag("emergencyStopped");
        var running = Flag("beltRunning");
        var boosted = root.TryGetProperty("speedBoosted", out var boost) && boost.GetBoolean();
        int? gasAnalog = root.TryGetProperty("gasAnalogValue", out var rawGas) && rawGas.ValueKind != JsonValueKind.Null ? rawGas.GetInt32() : null;
        var gasWarming = root.TryGetProperty("gasWarmingUp", out var warming) && warming.GetBoolean();
        var angle = root.GetProperty("sorterAngle").GetInt32();
        if (temperature is double t && (!double.IsFinite(t) || t < -40 || t > 125) ||
            humidity is double h && (!double.IsFinite(h) || h < 0 || h > 100) ||
            angle is < 0 or > 180 || gasAnalog is < 0 or > 1023 || stopped && running || boosted && !running)
            throw new IOException("장비 상태 값이 유효하지 않습니다.");
        return new(true, temperature, humidity, gas.ValueKind == JsonValueKind.Null ? null : gas.GetBoolean(),
            stopped, running, angle, Flag("sorting"), boosted, gasAnalog, gasWarming);
    }
    public static string CreateProductCommand(Guid id, DateTime capturedUtc, int travelMs, int holdMs, bool sort, DateTimeOffset now, int? boostMs = null)
    {
        if (boostMs.HasValue && boostMs is < 500 or > 60000) throw new ArgumentOutOfRangeException(nameof(boostMs));
        if (id == Guid.Empty || travelMs is < 500 or > 60000 || holdMs is < 200 or > 60000)
            throw new InvalidOperationException("제품 인식 또는 분류 시간 설정이 올바르지 않습니다.");
        var age = (int)Math.Ceiling((now - new DateTimeOffset(DateTime.SpecifyKind(capturedUtc,DateTimeKind.Utc))).TotalMilliseconds);
        if (age < 0 || age > 59000) throw new InvalidOperationException("제품 프레임 시간이 유효하지 않습니다.");
        // Account for this exact packet's wire transmission at 9600 baud (10 bits/character).
        var suffix = boostMs.HasValue ? $" {boostMs.Value}" : string.Empty;
        var preview = $"PRODUCT {id:N} {age + 100} {travelMs} {holdMs} {(sort ? 1 : 0)}{suffix}";
        age += (int)Math.Ceiling((preview.Length + 1) * 10000d / 9600d);
        return $"PRODUCT {id:N} {age} {travelMs} {holdMs} {(sort ? 1 : 0)}{suffix}";
    }
}
