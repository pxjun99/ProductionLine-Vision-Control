using System.Globalization;
using System.Text;

namespace ProductionLine.TrainingTool;

internal sealed record YoloAnnotation(int ClassId, RectangleF Box);

internal sealed class YoloProject
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp"];

    public string Root { get; }
    public string ImagesDirectory => Path.Combine(Root, "images");
    public string LabelsDirectory => Path.Combine(Root, "labels");
    public string DatasetDirectory => Path.Combine(Root, "dataset");
    public string ModelsDirectory => Path.Combine(Root, "models");
    public string ClassesPath => Path.Combine(Root, "classes.txt");
    public string DataYamlPath => Path.Combine(DatasetDirectory, "data.yaml");
    public List<string> Classes { get; private set; } = [];

    private YoloProject(string root) => Root = Path.GetFullPath(root);

    public static YoloProject Create(string root)
    {
        var project = new YoloProject(root);
        Directory.CreateDirectory(project.ImagesDirectory);
        Directory.CreateDirectory(project.LabelsDirectory);
        Directory.CreateDirectory(project.ModelsDirectory);
        if (!File.Exists(project.ClassesPath))
            File.WriteAllLines(project.ClassesPath, ["target_iphone", "other_phone", "crack"], Encoding.UTF8);
        project.ReloadClasses();
        return project;
    }

    public static YoloProject Open(string root)
    {
        var project = new YoloProject(root);
        if (!Directory.Exists(project.ImagesDirectory) || !File.Exists(project.ClassesPath))
            throw new InvalidOperationException("YOLO 프로젝트가 아닙니다. images 폴더와 classes.txt를 확인하세요.");
        Directory.CreateDirectory(project.LabelsDirectory);
        Directory.CreateDirectory(project.ModelsDirectory);
        project.ReloadClasses();
        return project;
    }

    public IReadOnlyList<string> GetImages() => Directory
        .EnumerateFiles(ImagesDirectory)
        .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public int ImportImages(IEnumerable<string> sourcePaths)
    {
        var imported = 0;
        foreach (var source in sourcePaths.Where(File.Exists))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase)) continue;
            var destination = UniqueDestination(Path.GetFileName(source));
            File.Copy(source, destination);
            imported++;
        }
        return imported;
    }

    public IReadOnlyList<YoloAnnotation> LoadAnnotations(string imagePath, Size imageSize)
    {
        var labelPath = LabelPath(imagePath);
        var result = new List<YoloAnnotation>();
        if (!File.Exists(labelPath) || imageSize.Width <= 0 || imageSize.Height <= 0) return result;

        foreach (var line in File.ReadLines(labelPath))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 || !int.TryParse(parts[0], out var classId)) continue;
            if (!TryDouble(parts[1], out var cx) || !TryDouble(parts[2], out var cy) ||
                !TryDouble(parts[3], out var width) || !TryDouble(parts[4], out var height)) continue;
            var box = new RectangleF(
                (float)((cx - width / 2) * imageSize.Width),
                (float)((cy - height / 2) * imageSize.Height),
                (float)(width * imageSize.Width),
                (float)(height * imageSize.Height));
            result.Add(new YoloAnnotation(classId, RectangleF.Intersect(box, new RectangleF(PointF.Empty, imageSize))));
        }
        return result;
    }

    public void SaveAnnotations(string imagePath, Size imageSize, IEnumerable<YoloAnnotation> annotations)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0) return;
        Directory.CreateDirectory(LabelsDirectory);
        var lines = annotations.Select(annotation =>
        {
            var box = RectangleF.Intersect(annotation.Box, new RectangleF(PointF.Empty, imageSize));
            var cx = (box.Left + box.Width / 2) / imageSize.Width;
            var cy = (box.Top + box.Height / 2) / imageSize.Height;
            var width = box.Width / imageSize.Width;
            var height = box.Height / imageSize.Height;
            return string.Create(CultureInfo.InvariantCulture,
                $"{annotation.ClassId} {cx:F6} {cy:F6} {width:F6} {height:F6}");
        });
        File.WriteAllLines(LabelPath(imagePath), lines, new UTF8Encoding(false));
    }

    public void AddClass(string className)
    {
        var normalized = className.Trim().Replace(' ', '_');
        if (normalized.Length == 0) throw new InvalidOperationException("클래스 이름을 입력하세요.");
        if (Classes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("이미 존재하는 클래스입니다.");
        Classes.Add(normalized);
        File.WriteAllLines(ClassesPath, Classes, new UTF8Encoding(false));
    }

    public DatasetSummary PrepareDataset(int validationPercent = 20)
    {
        var labeled = GetImages().Where(image => File.Exists(LabelPath(image))).ToList();
        if (labeled.Count < 2)
            throw new InvalidOperationException("라벨 저장이 완료된 사진이 최소 2장 필요합니다.");

        if (Directory.Exists(DatasetDirectory)) Directory.Delete(DatasetDirectory, true);
        foreach (var split in new[] { "train", "val" })
        {
            Directory.CreateDirectory(Path.Combine(DatasetDirectory, "images", split));
            Directory.CreateDirectory(Path.Combine(DatasetDirectory, "labels", split));
        }

        var shuffled = labeled.OrderBy(path => StableHash(Path.GetFileName(path))).ToList();
        var validationCount = Math.Clamp((int)Math.Round(shuffled.Count * validationPercent / 100.0), 1, shuffled.Count - 1);
        var validation = shuffled.Take(validationCount).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var image in shuffled)
        {
            var split = validation.Contains(image) ? "val" : "train";
            File.Copy(image, Path.Combine(DatasetDirectory, "images", split, Path.GetFileName(image)), true);
            File.Copy(LabelPath(image), Path.Combine(DatasetDirectory, "labels", split, Path.GetFileNameWithoutExtension(image) + ".txt"), true);
        }

        var yaml = new StringBuilder()
            .AppendLine($"path: '{DatasetDirectory.Replace("'", "''").Replace('\\', '/')}'")
            .AppendLine("train: images/train")
            .AppendLine("val: images/val")
            .AppendLine("names:");
        for (var i = 0; i < Classes.Count; i++)
            yaml.AppendLine($"  {i}: '{Classes[i].Replace("'", "''")}'");
        File.WriteAllText(DataYamlPath, yaml.ToString(), new UTF8Encoding(false));
        return new DatasetSummary(shuffled.Count - validationCount, validationCount, GetImages().Count - labeled.Count);
    }

    public string LabelPath(string imagePath) => Path.Combine(LabelsDirectory, Path.GetFileNameWithoutExtension(imagePath) + ".txt");

    private void ReloadClasses()
    {
        Classes = File.ReadAllLines(ClassesPath)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToList();
        if (Classes.Count == 0) throw new InvalidOperationException("classes.txt에 클래스가 없습니다.");
    }

    private string UniqueDestination(string fileName)
    {
        var candidate = Path.Combine(ImagesDirectory, fileName);
        var originalStem = Path.GetFileNameWithoutExtension(fileName);
        if (!File.Exists(candidate) && !GetImages().Any(path =>
                Path.GetFileNameWithoutExtension(path).Equals(originalStem, StringComparison.OrdinalIgnoreCase)))
            return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(ImagesDirectory, $"{stem}_{index}{extension}");
            var candidateStem = Path.GetFileNameWithoutExtension(candidate);
            if (!File.Exists(candidate) && !GetImages().Any(path =>
                    Path.GetFileNameWithoutExtension(path).Equals(candidateStem, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private static bool TryDouble(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static uint StableHash(string value)
    {
        uint hash = 2166136261;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return hash;
    }
}

internal sealed record DatasetSummary(int TrainCount, int ValidationCount, int UnlabeledCount);
