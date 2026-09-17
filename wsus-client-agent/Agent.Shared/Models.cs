namespace Agent.Shared;

/// <summary>
/// One update as last reported by a WUA search against WSUS. Rows are
/// keyed by UpdateId (the WUA Update.Identity.UpdateID GUID) and
/// replaced wholesale on every scan, except any row currently
/// Installing (the scan and install phases never run concurrently in
/// this agent, but this guards against a stale overwrite if that ever
/// changes).
/// </summary>
public class UpdateRecord
{
    public string UpdateId { get; set; } = "";
    public string? KbArticle { get; set; }
    public string Title { get; set; } = "";
    public string? Severity { get; set; }
    public double? SizeMb { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ErrorDetail { get; set; }
    public DateTime DiscoveredAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// A request from the tray app (running in the user's session) for the
/// service (running as SYSTEM) to act on. The tray inserts a row; the
/// service polls for unprocessed ones on its own short interval and
/// marks them processed once handled - this is the entire IPC
/// mechanism, deliberately just the shared SQLite file rather than a
/// named pipe or socket, since both processes already need to open it.
/// </summary>
public class AgentCommand
{
    public long Id { get; set; }
    public string Action { get; set; } = "";       // "InstallAll" | "InstallSelected" | "ScanNow"
    public string? UpdateIdsCsv { get; set; }       // used by InstallSelected
    public DateTime CreatedAt { get; set; }
    public bool Processed { get; set; }
}

public class AgentStatus
{
    public DateTime? LastScanAt { get; set; }
    public bool PendingReboot { get; set; }
    public string? LastError { get; set; }
    public bool ScanInProgress { get; set; }
    public bool InstallInProgress { get; set; }
}
