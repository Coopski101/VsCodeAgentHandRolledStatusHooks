namespace CopilotBeaconCore.Config;

public sealed class CoreConfig
{
    public int Port { get; set; } = 17321;
    public string VscodeProcessName { get; set; } = "Code";
    public int AfkThresholdSeconds { get; set; } = 30;
    public int PollIntervalMs { get; set; } = 250;
    public bool DebugLogging { get; set; } = false;
    public KeywordMatcherConfig Keywords { get; set; } = new();
}

public sealed class KeywordMatcherConfig
{
    public string RequiredApp { get; set; } = "copilot";
    public string[] WaitingKeywords { get; set; } = ["continue", "waiting", "approval", "proceed"];
    public string[] DoneKeywords { get; set; } = ["done", "finished", "complete"];
}
