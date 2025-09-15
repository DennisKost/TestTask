namespace WebApplication1.Options;

public class BrokerOptions(string directoryPath, string mode = "primitive", int clientTimeoutSeconds = 60, int coalescedTtlSeconds = 120)
{
    public string DirectoryPath { get; } = directoryPath;
    public string Mode { get; } = mode;
    public int ClientTimeoutSeconds { get; } = clientTimeoutSeconds;
    public int CoalescedTtlSeconds { get; } = coalescedTtlSeconds;
}


