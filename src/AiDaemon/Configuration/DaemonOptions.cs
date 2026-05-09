namespace AiDaemon.Configuration;

public class DaemonOptions
{
    public const string SectionName = "Daemon";

    public int PollIntervalSeconds { get; set; } = 60;
    public string AiUserLogin { get; set; } = "";
    public string WorktreeRoot { get; set; } = "";
    public string ClaudePath { get; set; } = "claude";
    public string PowerShellPath { get; set; } = "powershell.exe";
    public string GhPath { get; set; } = "gh";
    public string GhConfigDir { get; set; } = "";
    public List<string> RepoAllowlist { get; set; } = new();
    public List<string> ActionableReasons { get; set; } = new();
    public List<string> BotAuthorBlocklist { get; set; } = new();
    public int RcIdleTimeoutHours { get; set; } = 2;
    public string DataDir { get; set; } = @"C:\ProgramData\AiDaemon";

    public TriageOptions Triage { get; set; } = new();
    public NtfyOptions Ntfy { get; set; } = new();
}

public class TriageOptions
{
    public string Model { get; set; } = "haiku";
    public bool BareMode { get; set; } = true;
    public int MaxActionsPerThreadPerDay { get; set; } = 5;
    public List<string> L2DropPatterns { get; set; } = new();
}

public class NtfyOptions
{
    public string Server { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = "";
    public int PriorityNormal { get; set; } = 3;
    public int PriorityHigh { get; set; } = 4;
}
