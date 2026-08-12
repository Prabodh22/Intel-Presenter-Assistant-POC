namespace PptPoc.Core.Utilities;

public static class RuntimeArtifactPaths
{
    private const string WorkspaceMarker = "PptPoc.slnx";
    private const string LogsDirectoryName = "logs";

    public static string LogsDirectory => GetLogsDirectory();

    public static string GetLogsDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("PPTPOC_ARTIFACTS_DIR", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("PPTPOC_ARTIFACTS_DIR", EnvironmentVariableTarget.User);

        if (!string.IsNullOrWhiteSpace(configured))
            return EnsureDirectory(Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured)));

        var workspaceRoot = FindWorkspaceRoot(AppContext.BaseDirectory)
            ?? FindWorkspaceRoot(Environment.CurrentDirectory);

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
            return EnsureDirectory(Path.Combine(workspaceRoot, LogsDirectoryName));

        var appLocal = Path.Combine(AppContext.BaseDirectory, LogsDirectoryName);
        try
        {
            return EnsureDirectory(appLocal);
        }
        catch
        {
            var userLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Intel_Smart_Presenter_Assistant",
                LogsDirectoryName);
            return EnsureDirectory(userLocal);
        }
    }

    public static string ResolveInLogs(string configuredPath, string fallbackFileName)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return Path.Combine(GetLogsDirectory(), fallbackFileName);

        if (Path.IsPathRooted(configuredPath))
            return configuredPath;

        var fileName = Path.GetFileName(configuredPath);
        return Path.Combine(GetLogsDirectory(), string.IsNullOrWhiteSpace(fileName) ? fallbackFileName : fileName);
    }

    private static string? FindWorkspaceRoot(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return null;

        var directory = new DirectoryInfo(startDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, WorkspaceMarker)))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}