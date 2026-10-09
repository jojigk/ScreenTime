namespace ScreenTime.Common;

/// <summary>
/// Filesystem and IPC locations shared by the service and the agent.
/// </summary>
public static class Paths
{
    public const string ProgramDataDirectoryName = "ScreenTime";

    public static string ProgramDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProgramDataDirectoryName);

    public static string ConfigFilePath { get; } = Path.Combine(ProgramDataDirectory, "config.json");

    /// <summary>Directory holding one usage-state file per Windows account (keyed by SID).</summary>
    public static string StateDirectory { get; } = Path.Combine(ProgramDataDirectory, "state");

    /// <summary>SIDs (e.g. "S-1-5-21-...") are already filesystem-safe, so no sanitizing is needed.</summary>
    public static string StateFilePathFor(string sid) => Path.Combine(StateDirectory, $"{sid}.json");

    /// <summary>
    /// Name only (not the "\\.\pipe\" prefix) — <see cref="System.IO.Pipes.NamedPipeServerStream"/>
    /// and <see cref="System.IO.Pipes.NamedPipeClientStream"/> both add that themselves.
    /// </summary>
    public const string PipeName = "ScreenTime.Agent";
}
