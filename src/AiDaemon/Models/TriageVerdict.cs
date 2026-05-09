using System.Text.Json.Serialization;

namespace AiDaemon.Models;

public enum TriageAction
{
    Actionable,
    Drop,
}

public record TriageVerdict(
    TriageAction Action,
    string Why,
    string Summary,
    double Confidence)
{
    public static TriageVerdict Drop(string why, string summary = "", double confidence = 1.0)
        => new(TriageAction.Drop, why, summary, confidence);

    public static TriageVerdict Actionable(string why, string summary = "", double confidence = 1.0)
        => new(TriageAction.Actionable, why, summary, confidence);
}

/// <summary>The shape claude returns under "structured_output" when triaging.</summary>
public class TriageStructuredOutput
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("why")]
    public string Why { get; set; } = "";
}
