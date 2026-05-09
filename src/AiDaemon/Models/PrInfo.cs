using System.Text.Json.Serialization;

namespace AiDaemon.Models;

public class PrInfo
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("head")]
    public PrRef Head { get; set; } = new();

    [JsonPropertyName("base")]
    public PrRef Base { get; set; } = new();
}

public class PrRef
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = "";

    [JsonPropertyName("sha")]
    public string Sha { get; set; } = "";
}
