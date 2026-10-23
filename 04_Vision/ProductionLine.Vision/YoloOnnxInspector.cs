using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using ProductionLine.Shared;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace ProductionLine.Vision;

/// <summary>
/// 학습 도구가 만든 Ultralytics YOLO ONNX 모델을 C#에서 실행합니다.
/// 한 인스턴스를 재사용해야 매 프레임마다 모델을 다시 읽지 않습니다.
/// </summary>
public sealed partial class YoloOnnxInspector : IDisposable
{
    public const string TargetIphone = "target_iphone";
    public const string OtherPhone = "other_phone";
    public const string Crack = "crack";
    public const string CrackPhone = "crack_phone";
    // all_phones 통합 모델(v2~v4)의 불량 클래스. 기종과 상관없이 깨진 폰을 뜻합니다.
    // v5부터는 기종별 불량 클래스(iphone12_promax_crack, iphone8_crack)를 씁니다 → IsCrackModelClass
    public const string CrackIphone = "crack_iphone";
    public const string Phone = "phone";
    public const string Iphone16 = "iphone_16";
    public const string Iphone14 = "iphone_14";
    public const string Iphone15 = "iphone_15";
    public const string Iphone15Pro = "iphone_15_pro";
    public const string Iphone8 = "iphone_8";
    public const string Iphone12ProMax = "iphone_12_pro_max";
    public const string Iphone12Pro = "iphone_12_pro";
    private const float DamageConfidenceThreshold = 0.10f;

    private readonly string[] _classNames;
    private readonly bool _classificationEnabled;
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private readonly Mat? _emptyBeltReferenceGray;
    private bool _disposed;

    public string ModelPath { get; }
    public float ConfidenceThreshold { get; }
    public float IouThreshold { get; }
    private bool CanClassifySingleModel => _classificationEnabled && (IsUnifiedModel ||
        _classNames.Contains(TargetIphone) && _classNames.Contains(OtherPhone) &&
        (_classNames.Contains(Crack) || _classNames.Contains(CrackPhone)));
    // 불량(crack_iphone 또는 기종_crack)과 기종 클래스를 한 모델에서 함께 학습한 통합 모델입니다.
    private bool IsUnifiedModel => (_classNames.Contains(CrackIphone) || _classNames.Any(IsCrackModelClass)) && _classNames.Any(IsSpecificPhoneClass);
    public bool CanClassifyProducts => CanClassifySingleModel || _additionalModels.Any(x => x.CanClassifyProducts);
    public IReadOnlyList<string> ClassNames => _combinedNames ?? _classNames;
    public bool HasPhoneModelClasses => ClassNames.Any(IsSpecificPhoneClass);
    public string CapabilityDescription => _additionalModels.Count > 0 ? $"전체 학습 {LoadedModelPaths.Count}종 통합 · 기종·색상 후보 / 파손 검사" : CanClassifyProducts ? "기종·깨짐 검사" : HasPhoneModelClasses ? "아이폰 기종 검출 · 품질 판정 미지원" : "휴대폰 위치 검출 전용";
    public static bool IsPhoneClass(string className) => className is Phone or TargetIphone or OtherPhone or CrackPhone or CrackIphone || IsSpecificPhoneClass(className) || IsCrackModelClass(className);
    // 화면·DB에 기종으로 남길 이름입니다. 기종_crack(예: iphone_8_crack)도 이름 그대로 남깁니다.
    public static bool IsPhoneModelClass(string className) => IsSpecificPhoneClass(className) || IsCrackModelClass(className);
    // 깨진 휴대폰만 학습한 기종별 불량 클래스(예: iphone12_pro_max_crack, iphone_8_crack). 나오면 무조건 불량입니다. (김하균 2026-10-23)
    public static bool IsCrackModelClass(string className) =>
        className.StartsWith("iphon", StringComparison.Ordinal) && className.EndsWith("_crack", StringComparison.OrdinalIgnoreCase);
    public static string DisplayClassName(string className) => className switch
    {
        Crack => "crack_phone",
        CrackPhone => "crack_phone",
        TargetIphone => string.Empty,
        OtherPhone => string.Empty,
        Phone => "휴대폰",
        // v5부터 클래스 이름을 화면·DB·보고서에 그대로 씁니다. v4의 철자(e 빠짐)만 v5 이름으로 바꿉니다. (김하균 2026-10-23)
        "iphon16_white" => "iphone16_white",
        _ => className
    };
    private static bool IsSpecificPhoneClass(string className)
    {
        // 기종_crack은 정상 기종이 아니라 불량 클래스로 따로 다룹니다.
        if (IsCrackModelClass(className)) return false;
        // all_phones 학습 데이터는 16 화이트를 'iphon16_white'(e 빠짐)로 정했으므로 'iphon' 뒤 숫자도 허용합니다.
        var rest = className.StartsWith("iphone", StringComparison.Ordinal) ? className["iphone".Length..]
            : className.StartsWith("iphon", StringComparison.Ordinal) ? className["iphon".Length..]
            : string.Empty;
        return rest.Length > 0 && (char.IsDigit(rest[0]) || rest[0] == '_' && rest.Length > 1);
    }

    public YoloOnnxInspector(
        string? modelPath = null,
        float confidenceThreshold = 0.25f,
        float iouThreshold = 0.45f)
    {
        ModelPath = ResolveModelPath(modelPath);
        ConfidenceThreshold = Math.Clamp(confidenceThreshold, 0.01f, 0.99f);
        IouThreshold = Math.Clamp(iouThreshold, 0.01f, 0.99f);

        _session = new InferenceSession(ModelPath);
        try
        {
            _classNames = ReadClassNames(_session);
            _classificationEnabled = ReadClassificationEnabled(ModelPath) ?? (IsUnifiedModel || !HasPhoneModelClasses);
            _inputName = _session.InputMetadata.Keys.Single();
            _emptyBeltReferenceGray = LoadEmptyBeltReference();
            var dimensions = _session.InputMetadata[_inputName].Dimensions;
            if (dimensions.Length != 4 || dimensions[2] <= 0 || dimensions[3] <= 0)
                throw new InvalidOperationException("지원하지 않는 YOLO 입력 크기입니다.");
            _inputHeight = dimensions[2];
            _inputWidth = dimensions[3];
        }
        catch { _session.Dispose(); throw; }
    }

    public YoloInspectionAnalysis Inspect(byte[] imageBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _additionalModels.Count == 0 ? InspectSingle(imageBytes) : InspectCombined(imageBytes);
    }

    private YoloInspectionAnalysis InspectSingle(byte[] imageBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var image = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        if (image.Empty()) return YoloInspectionAnalysis.Failed("이미지를 읽지 못했습니다.");
        if (IsEmptyConveyorFrame(image))
            return new(false, InspectionResultType.Undetermined,
                "빈 컨베이어로 확인되어 휴대폰 검출을 대기합니다.", [], image.ToBytes(".jpg"));

        var (tensor, scale, padX, padY) = CreateInput(image);
        var input = NamedOnnxValue.CreateFromTensor(_inputName, tensor);
        using var results = _session.Run([input]);
        var output = results.First().AsTensor<float>();
        var detections = ApplyNms(ParseOutput(output, image.Size(), scale, padX, padY))
            .Where(value => value.ClassName is not TargetIphone and not Crack)
            .ToArray();
        if (IsUnifiedModel && CanClassifySingleModel) return InspectUnified(image, detections);

        using var annotated = image.Clone();
        foreach (var detection in detections)
            DrawDetection(annotated, detection);
        Cv2.ImEncode(".jpg", annotated, out var resultImage);

        // 기종_crack만 학습한 모델도 그 클래스가 나오면 불량입니다.
        var crackModel = detections.Where(value => IsCrackModelClass(value.ClassName)).MaxBy(value => value.Confidence);
        if (crackModel != null)
            return new YoloInspectionAnalysis(true, InspectionResultType.Defect,
                $"{crackModel.ClassName}(파손)이 검출되어 불량으로 판정했습니다.", detections, resultImage);

        var hasOther = detections.Any(value => value.ClassName == OtherPhone);
        var hasCrack = detections.Any(value => value.ClassName == CrackPhone);

        if (!CanClassifySingleModel)
        {
            var phoneFound = detections.Any(value => IsPhoneClass(value.ClassName));
            var detectedNames = string.Join(", ", detections.Where(value => IsPhoneClass(value.ClassName))
                .Select(value => value.ClassName).Distinct());
            var detectedReason = HasPhoneModelClasses
                ? $"휴대폰 검출: {detectedNames}. 허용 기종·깨짐 검사 기준이 검증되지 않아 정상/불량은 판정하지 않습니다."
                : "휴대폰 위치를 검출했습니다. 현재 모델은 아이폰 기종·표면 깨짐 판정을 지원하지 않습니다.";
            return new YoloInspectionAnalysis(phoneFound, InspectionResultType.Undetermined,
                phoneFound ? detectedReason
                    : $"신뢰도 {ConfidenceThreshold:P0} 이상인 휴대폰을 찾지 못했습니다.", detections, resultImage);
        }

        if (hasCrack)
            return new YoloInspectionAnalysis(true, InspectionResultType.Defect,
                "표면 깨짐이 검출되었습니다.", detections, resultImage);
        if (hasOther)
            return new YoloInspectionAnalysis(true, InspectionResultType.Defect,
                "검사 대상 아이폰이 아닌 제품이 검출되었습니다.", detections, resultImage);
        return new YoloInspectionAnalysis(false, InspectionResultType.Undetermined,
            $"신뢰도 {ConfidenceThreshold:P0} 이상인 휴대폰을 찾지 못했습니다.", detections, resultImage);
    }

    // all_phones 통합 모델: 불량 클래스(v5 기종_crack, v4 crack_iphone)가 하나라도 있으면 불량, 아니면 가장 큰 박스의 기종으로 정상입니다.
    private YoloInspectionAnalysis InspectUnified(Mat image, IReadOnlyList<YoloDetection> raw)
    {
        var detections = RemoveNestedBoxes(raw);
        using var annotated = image.Clone();
        var wholePhone = detections.MaxBy(x => x.Box.Width * x.Box.Height);
        if (wholePhone == null)
            return new(false, InspectionResultType.Undetermined,
                $"신뢰도 {ConfidenceThreshold:P0} 이상인 휴대폰을 찾지 못했습니다.", detections, annotated.ToBytes(".jpg"));

        var model = detections.Where(x => IsSpecificPhoneClass(x.ClassName)).MaxBy(x => x.Box.Width * x.Box.Height);
        var modelName = model == null ? "기종 미확인" : DisplayClassName(model.ClassName);
        // 기종_crack이 하나라도 있으면 다른 정상 기종 박스가 같이 나와도 불량입니다.
        var crackModel = detections.Where(x => IsCrackModelClass(x.ClassName)).MaxBy(x => x.Confidence);
        if (crackModel != null)
        {
            DrawDetection(annotated, wholePhone with { ClassName = crackModel.ClassName }, crackModel.ClassName);
            return new(true, InspectionResultType.Defect,
                $"{crackModel.ClassName}(파손)이 검출되어 불량으로 판정했습니다.", detections, annotated.ToBytes(".jpg"));
        }
        if (detections.Any(x => x.ClassName == CrackIphone))
        {
            DrawDetection(annotated, wholePhone with { ClassName = CrackIphone },
                model == null ? CrackIphone : $"{CrackIphone} {modelName}");
            return new(true, InspectionResultType.Defect,
                $"crack_iphone(파손)이 검출되어 불량으로 판정했습니다. 검출 기종: {modelName}.", detections, annotated.ToBytes(".jpg"));
        }
        DrawDetection(annotated, model!, modelName);
        return new(true, InspectionResultType.Normal,
            $"검출 기종: {modelName}. 파손이 검출되지 않아 정상으로 판정했습니다.", detections, annotated.ToBytes(".jpg"));
    }

    // 통합 모델 라벨은 폰 전체 박스 안에 카메라 모듈(또는 깨진 부위) 박스를 같은 종류로 그렸습니다.
    // 큰 박스 안에 80% 이상 들어간 같은 종류의 작은 박스는 폰 위치·개수·기종 판단에서 뺍니다.
    private static IReadOnlyList<YoloDetection> RemoveNestedBoxes(IEnumerable<YoloDetection> values)
    {
        static string Kind(string name) => IsSpecificPhoneClass(name) ? "iphone-model" : name;
        var kept = new List<YoloDetection>();
        foreach (var candidate in values.OrderByDescending(x => x.Box.Width * x.Box.Height))
        {
            var area = candidate.Box.Width * candidate.Box.Height;
            var nested = kept.Any(outer => Kind(outer.ClassName) == Kind(candidate.ClassName) &&
                Area(System.Drawing.RectangleF.Intersect(outer.Box, candidate.Box)) >= area * .8f);
            if (!nested) kept.Add(candidate);
        }
        return kept;

        static float Area(System.Drawing.RectangleF box) => box.Width <= 0 || box.Height <= 0 ? 0 : box.Width * box.Height;
    }

    private (DenseTensor<float> Tensor, float Scale, float PadX, float PadY) CreateInput(Mat image)
    {
        var scale = Math.Min(_inputWidth / (float)image.Width, _inputHeight / (float)image.Height);
        var resizedWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
        var resizedHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
        var padX = (_inputWidth - resizedWidth) / 2f;
        var padY = (_inputHeight - resizedHeight) / 2f;

        using var resized = new Mat();
        Cv2.Resize(image, resized, new Size(resizedWidth, resizedHeight), 0, 0, InterpolationFlags.Linear);
        using var letterboxed = new Mat(new Size(_inputWidth, _inputHeight), MatType.CV_8UC3, new Scalar(114, 114, 114));
        var left = (int)Math.Floor(padX);
        var top = (int)Math.Floor(padY);
        using (var region = new Mat(letterboxed, new Rect(left, top, resizedWidth, resizedHeight)))
            resized.CopyTo(region);
        // BGR→RGB, 0~1 변환, HWC→CHW 배치를 OpenCV 한 번으로 처리합니다(픽셀 단위 반복보다 훨씬 빠름).
        using var blob = CvDnn.BlobFromImage(letterboxed, 1 / 255.0, new Size(_inputWidth, _inputHeight),
            new Scalar(), swapRB: true, crop: false);
        var data = new float[3 * _inputHeight * _inputWidth];
        System.Runtime.InteropServices.Marshal.Copy(blob.Data, data, 0, data.Length);
        var tensor = new DenseTensor<float>(data, [1, 3, _inputHeight, _inputWidth]);
        return (tensor, scale, left, top);
    }

    private List<YoloDetection> ParseOutput(Tensor<float> output, OpenCvSharp.Size originalSize, float scale, float padX, float padY)
    {
        if (output.Dimensions.Length != 3)
            throw new InvalidOperationException("지원하지 않는 YOLO 출력 형식입니다.");

        var featuresFirst = output.Dimensions[1] == _classNames.Length + 4;
        var featuresLast = output.Dimensions[2] == _classNames.Length + 4;
        if (!featuresFirst && !featuresLast)
            throw new InvalidOperationException($"YOLO 출력 클래스 수가 맞지 않습니다. 출력: {string.Join('x', output.Dimensions.ToArray())}");

        var candidateCount = featuresFirst ? output.Dimensions[2] : output.Dimensions[1];
        float Value(int feature, int candidate) => featuresFirst
            ? output[0, feature, candidate]
            : output[0, candidate, feature];

        var detections = new List<YoloDetection>();
        for (var candidate = 0; candidate < candidateCount; candidate++)
        {
            var classId = 0;
            var confidence = Value(4, candidate);
            for (var index = 1; index < _classNames.Length; index++)
            {
                var score = Value(4 + index, candidate);
                if (score <= confidence) continue;
                confidence = score;
                classId = index;
            }
            var className = _classNames[classId];
            // 작은 균열은 전체 휴대폰보다 신뢰도가 낮게 나오므로 파손 클래스만 별도 기준을 사용합니다.
            var requiredConfidence = className is Crack or CrackPhone
                ? Math.Min(ConfidenceThreshold, DamageConfidenceThreshold)
                : ConfidenceThreshold;
            if (confidence < requiredConfidence) continue;

            var centerX = Value(0, candidate);
            var centerY = Value(1, candidate);
            var width = Value(2, candidate);
            var height = Value(3, candidate);
            var left = (centerX - width / 2 - padX) / scale;
            var top = (centerY - height / 2 - padY) / scale;
            var right = (centerX + width / 2 - padX) / scale;
            var bottom = (centerY + height / 2 - padY) / scale;

            left = Math.Clamp(left, 0, originalSize.Width - 1);
            top = Math.Clamp(top, 0, originalSize.Height - 1);
            right = Math.Clamp(right, 0, originalSize.Width);
            bottom = Math.Clamp(bottom, 0, originalSize.Height);
            if (right - left < 2 || bottom - top < 2) continue;

            detections.Add(new YoloDetection(
                classId,
                className,
                confidence,
                new System.Drawing.RectangleF(left, top, right - left, bottom - top)));
        }
        return detections;
    }

    private IReadOnlyList<YoloDetection> ApplyNms(IEnumerable<YoloDetection> values)
    {
        var kept = new List<YoloDetection>();
        foreach (var candidate in values.OrderByDescending(value => value.Confidence))
        {
            // 목표/다른 휴대폰은 같은 제품 그룹으로 경쟁시키고, crack 박스는 별도로 유지합니다.
            var candidateGroup = DetectionGroup(candidate.ClassName);
            var overlaps = kept.Any(existing =>
                DetectionGroup(existing.ClassName) == candidateGroup &&
                IntersectionOverUnion(existing.Box, candidate.Box) > IouThreshold);
            if (!overlaps) kept.Add(candidate);
        }
        return kept;
    }

    private static float IntersectionOverUnion(System.Drawing.RectangleF first, System.Drawing.RectangleF second)
    {
        var intersection = System.Drawing.RectangleF.Intersect(first, second);
        if (intersection.Width <= 0 || intersection.Height <= 0) return 0;
        var intersectionArea = intersection.Width * intersection.Height;
        var unionArea = first.Width * first.Height + second.Width * second.Height - intersectionArea;
        return unionArea <= 0 ? 0 : intersectionArea / unionArea;
    }

    private static void DrawDetection(Mat image, YoloDetection detection, string? labelOverride = null)
    {
        // target/other/crack/phone은 내부 판정용 클래스이므로 결과 화면에는 직접 노출하지 않습니다.
        if (detection.ClassName is TargetIphone or OtherPhone or Crack or Phone) return;
        var color = detection.ClassName switch
        {
            TargetIphone => new Scalar(0, 200, 0),
            Phone => new Scalar(255, 180, 0),
            OtherPhone => new Scalar(0, 0, 255),
            Crack => new Scalar(0, 0, 255),
            CrackPhone or CrackIphone => new Scalar(0, 0, 255),
            _ when IsCrackModelClass(detection.ClassName) => new Scalar(0, 0, 255),
            _ when IsSpecificPhoneClass(detection.ClassName) => new Scalar(0, 200, 0),
            _ => new Scalar(255, 255, 255)
        };
        var rectangle = new Rect(
            (int)detection.Box.X,
            (int)detection.Box.Y,
            Math.Max(1, (int)detection.Box.Width),
            Math.Max(1, (int)detection.Box.Height));
        Cv2.Rectangle(image, rectangle, color, 3);
        Cv2.PutText(image, labelOverride ?? detection.ClassName,
            new Point(rectangle.X, Math.Max(25, rectangle.Y - 8)),
            HersheyFonts.HersheySimplex, 0.75, color, 2);
    }

    private static string ResolveModelPath(string? modelPath)
    {
        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            var explicitPath = Path.GetFullPath(modelPath);
            if (File.Exists(explicitPath)) return explicitPath;
            throw new FileNotFoundException("YOLO ONNX 모델을 찾지 못했습니다.", explicitPath);
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Models", "best.onnx"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models", "best.onnx"))
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Models/best.onnx를 찾지 못했습니다. 모델의 출력 복사 설정을 확인하세요.");
    }

    private static string[] ReadClassNames(InferenceSession session)
    {
        if (!session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var text))
            throw new InvalidOperationException("ONNX 모델에 클래스 이름(names) 정보가 없습니다.");
        // Ultralytics의 Python 딕셔너리 표기를 문자열로만 읽습니다. 코드는 실행하지 않습니다.
        var matches = Regex.Matches(text, @"['""]?(?<id>\d+)['""]?\s*:\s*['""](?<name>[^'""]+)['""]");
        var entries = matches.Select(match => (Id: int.Parse(match.Groups["id"].Value), Name: match.Groups["name"].Value)).OrderBy(entry => entry.Id).ToArray();
        if (entries.Length == 0 || entries.Where((entry, index) => entry.Id != index).Any() ||
            entries.Any(entry => !IsPhoneClass(entry.Name) && entry.Name != Crack))
            throw new InvalidOperationException("지원하지 않는 ONNX 클래스입니다. phone, target_iphone, other_phone, crack, crack_phone, iphone_로 시작하는 기종 클래스 또는 기종_crack 클래스를 지원합니다.");
        return entries.Select(entry => entry.Name).ToArray();
    }

    private static bool? ReadClassificationEnabled(string modelPath)
    {
        var infoPath = Path.Combine(Path.GetDirectoryName(modelPath)!, "model_info.json");
        if (!File.Exists(infoPath)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(infoPath));
        if (!document.RootElement.TryGetProperty("classificationAvailable", out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("model_info.json의 classificationAvailable 값은 true 또는 false여야 합니다.");
        return value.GetBoolean();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var model in _additionalModels) model.Dispose();
        _emptyBeltReferenceGray?.Dispose();
        _session.Dispose();
    }
}

public sealed record YoloDetection(
    int ClassId,
    string ClassName,
    float Confidence,
    System.Drawing.RectangleF Box);

public sealed record YoloInspectionAnalysis(
    bool ObjectFound,
    InspectionResultType Result,
    string Reason,
    IReadOnlyList<YoloDetection> Detections,
    byte[] ResultImage)
{
    public static YoloInspectionAnalysis Failed(string reason) =>
        new(false, InspectionResultType.Undetermined, reason, [], []);
}
