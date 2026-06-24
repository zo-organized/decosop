namespace DecoSOP.Services;

/// <summary>
/// Configures the watched folders that back the SOP and Document modules.
/// Each Root is any local directory path — a OneDrive-synced folder, a UNC/mapped
/// network share, or a plain local folder. A module is active when its Root is set
/// and the directory exists.
/// </summary>
public class FolderSyncOptions
{
    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 300;
    public int DebounceMs { get; set; } = 2000;
    public RootConfig Sop { get; set; } = new();
    public RootConfig Doc { get; set; } = new();

    public class RootConfig
    {
        /// <summary>Where the server reads/writes files (OneDrive-synced folder, share, or local).</summary>
        public string? Root { get; set; }

        /// <summary>Client-reachable base for "Open in Office" links: a UNC share (\\server\SOPs)
        /// or a SharePoint/OneDrive web URL (https://tenant.sharepoint.com/.../SOPs). If empty,
        /// the server's local path is used (only works when the client is the server).</summary>
        public string? OpenBase { get; set; }
    }
}
