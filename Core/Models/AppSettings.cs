using System.Text.Json.Serialization;

namespace ChGhost.Core.Models;

public sealed class AppSettings
{
    public int EngineThreads { get; set; } = 4;
    public int EngineHashMb { get; set; } = 128;
    public int TargetElo { get; set; } = 2600;
    public bool FullStrength { get; set; } = true;
    public int MoveTimeBlitzMs { get; set; } = 180;
    public int MoveTimeRapidMs { get; set; } = 550;
    public int ScanIntervalBlitzMs { get; set; } = 120;
    public int ScanIntervalRapidMs { get; set; } = 450;
    public int CommandTimeoutMs { get; set; } = 3500;
    public string RecognizerMode { get; set; } = "Auto";
    public string OnnxModelPath { get; set; } = "models/pieces_64x64.onnx";

    [JsonIgnore]
    public int EffectiveEngineThreads => EngineThreads == 0
        ? Math.Clamp(Environment.ProcessorCount - 2, 1, 16)
        : Math.Clamp(EngineThreads, 1, 16);

    public static AppSettings CreateDefaults() => new()
    {
        EngineThreads = 4,
        EngineHashMb = 128,
        TargetElo = 2600,
        FullStrength = true,
        MoveTimeBlitzMs = 180,
        MoveTimeRapidMs = 550,
        ScanIntervalBlitzMs = 120,
        ScanIntervalRapidMs = 450,
        CommandTimeoutMs = 3500
        , RecognizerMode = "Auto"
        , OnnxModelPath = "models/pieces_64x64.onnx"
    };

    public AppSettings Normalize()
    {
        EngineThreads = EngineThreads == 0 ? 0 : Math.Clamp(EngineThreads, 1, 16);
        EngineHashMb = Math.Clamp(EngineHashMb, 16, 4096);
        TargetElo = Math.Clamp(TargetElo, 1000, 3200);
        MoveTimeBlitzMs = Math.Clamp(MoveTimeBlitzMs, 10, 60_000);
        MoveTimeRapidMs = Math.Clamp(MoveTimeRapidMs, 10, 60_000);
        ScanIntervalBlitzMs = Math.Clamp(ScanIntervalBlitzMs, 50, 10_000);
        ScanIntervalRapidMs = Math.Clamp(ScanIntervalRapidMs, 50, 10_000);
        CommandTimeoutMs = Math.Clamp(CommandTimeoutMs, 250, 60_000);
        RecognizerMode = RecognizerMode?.Trim().ToLowerInvariant() switch
        {
            "onnx" => "Onnx",
            "template" => "Template",
            _ => "Auto"
        };
        OnnxModelPath = string.IsNullOrWhiteSpace(OnnxModelPath)
            ? "models/pieces_64x64.onnx"
            : OnnxModelPath.Trim();
        return this;
    }
}
