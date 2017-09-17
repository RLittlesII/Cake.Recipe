BuildParameters.Tasks.InstallDotNetCoreTask = Task("InstallDotNetCoreTask")
.WithCriteria(ToolSettings.InstallDotNetSdkVersion != null)
.Does(() =>
{
        InstallDotNetSdk(env, buildPlan,
            version: buildPlan.DotNetVersion,
            installFolder: env.Folders.DotNetSdk);
});


void InstallDotNetSdk(BuildEnvironment env, BuildPlan plan, string version, string installFolder = "./.donet")
{
    if (!DirectoryHelper.Exists(installFolder))
    {
        DirectoryHelper.Create(installFolder);
    }

    var scriptFileName = $"dotnet-install.{env.ShellScriptFileExtension}";
    var scriptFilePath = CombinePaths(installFolder, scriptFileName);
    var url = $"{plan.DotNetInstallScriptURL}/{scriptFileName}";

    using (var client = new WebClient())
    {
        client.DownloadFile(url, scriptFilePath);
    }

    if (!Platform.Current.IsWindows)
    {
        Run("chmod", $"+x '{scriptFilePath}'");
    }

    var argList = new List<string>();

    argList.Add("-Channel");
    argList.Add(plan.DotNetChannel);

    if (!string.IsNullOrEmpty(version))
    {
        argList.Add("-Version");
        argList.Add(version);
    }

    argList.Add("-InstallDir");
    argList.Add(installFolder);

    Run(env.ShellCommand, $"{env.ShellArgument} {scriptFilePath} {string.Join(" ", argList)}").ExceptionOnError($"Failed to Install .NET Core SDK {version}");
}

public class BuildEnvironment
{
    public string WorkingDirectory { get; }
    public Folders Folders { get; }

    public string DotNetCommand { get; }
    public string LegacyDotNetCommand { get; }

    public string ShellCommand { get; }
    public string ShellArgument { get; }
    public string ShellScriptFileExtension { get; }

    public MonoRuntime[] MonoRuntimes { get; }
    public MonoRuntime CurrentMonoRuntime { get; }

    public BuildEnvironment(bool useGlobalDotNetSdk)
    {
        this.WorkingDirectory = PathHelper.GetFullPath(
            System.IO.Directory.GetCurrentDirectory());
        this.Folders = new Folders(this.WorkingDirectory);

        this.DotNetCommand = useGlobalDotNetSdk
            ? "dotnet"
            : PathHelper.Combine(this.Folders.DotNetSdk, "dotnet");

        this.LegacyDotNetCommand = PathHelper.Combine(this.Folders.LegacyDotNetSdk, "dotnet");

        this.ShellCommand = Platform.Current.IsWindows ? "powershell" : "bash";
        this.ShellArgument = Platform.Current.IsWindows ? "-NoProfile /Command" : "-C";
        this.ShellScriptFileExtension = Platform.Current.IsWindows ? "ps1" : "sh";

        this.MonoRuntimes = new []
        {
            new MonoRuntime("osx", this.Folders.MonoRuntimeMacOS, "mono.osx"),
            new MonoRuntime("linux-x86", this.Folders.MonoRuntimeLinux32, "mono.linux-x86"),
            new MonoRuntime("linux-x64", this.Folders.MonoRuntimeLinux64, "mono.linux-x86_64")
        };

        if (Platform.Current.IsMacOS)
        {
            this.CurrentMonoRuntime = this.MonoRuntimes[0];
        }
        else if (Platform.Current.IsLinux && Platform.Current.Is32Bit)
        {
            this.CurrentMonoRuntime = this.MonoRuntimes[1];
        }
        else if (Platform.Current.IsLinux && Platform.Current.Is64Bit)
        {
            this.CurrentMonoRuntime = this.MonoRuntimes[2];
        }
    }
}

/// <summary>
///  Class representing build.json
/// </summary>
public class BuildPlan
{
    public string DotNetInstallScriptURL { get; set; }
    public string DotNetChannel { get; set; }
    public string DotNetVersion { get; set; }
    public string LegacyDotNetVersion { get; set; }
    public string RequiredMonoVersion { get; set; }
    public string DownloadURL { get; set; }
    public string MonoRuntimeMacOS { get; set; }
    public string MonoRuntimeLinux32 { get; set; }
    public string MonoRuntimeLinux64 { get; set; }
    public string MonoFramework { get; set; }
    public string MonoMSBuildRuntime { get; set; }
    public string MonoMSBuildLib { get; set; }
    public string[] HostProjects { get; set; }
    public string[] TestProjects { get; set; }
    public string[] TestAssets { get; set; }
    public string[] LegacyTestAssets { get; set; }

    public static BuildPlan Load(BuildEnvironment env)
    {
        var buildJsonPath = PathHelper.Combine(env.WorkingDirectory, "build.json");
        return JsonConvert.DeserializeObject<BuildPlan>(
            System.IO.File.ReadAllText(buildJsonPath));
    }
}

/// <summary>
///  Run the given command with the given arguments.
/// </summary>
/// <param name="exec">Command to run</param>
/// <param name="arguments">Arguments</param>
/// <param name="runOptions">Optional settings</param>
/// <returns>The exit status for further queries</returns>
ExitStatus Run(string command, string arguments, RunOptions runOptions)
{
    var workingDirectory = runOptions.WorkingDirectory ?? System.IO.Directory.GetCurrentDirectory();

    Context.Log.Write(Verbosity.Diagnostic, LogLevel.Debug, "Run:");
    Context.Log.Write(Verbosity.Diagnostic, LogLevel.Debug, "  Command: {0}", command);
    Context.Log.Write(Verbosity.Diagnostic, LogLevel.Debug, "  Arguments: {0}", arguments);
    Context.Log.Write(Verbosity.Diagnostic, LogLevel.Debug, "  CWD: {0}", workingDirectory);

    var startInfo = new ProcessStartInfo(command, arguments)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = runOptions.Output != null
    };
    if (runOptions.Environment != null)
    {
        foreach (var item in runOptions.Environment)
        {
            startInfo.EnvironmentVariables.Add(item.Key, item.Value);
        }
    }

    var process = System.Diagnostics.Process.Start(startInfo);

    if (runOptions.Output != null)
    {
        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                runOptions.Output.Add(e.Data);
            }
        };

        process.BeginOutputReadLine();
    }

    if (runOptions.TimeOut == 0)
    {
        process.WaitForExit();
        return new ExitStatus(process.ExitCode);
    }
    else
    {
        bool finished = process.WaitForExit(runOptions.TimeOut);
        if (finished)
        {
            return new ExitStatus(process.ExitCode);
        }
        else
        {
            KillProcessTree(process);
            return new ExitStatus(0, true);
        }
    }
}

/// <summary>
///  Wrapper for the exit code and state.
///  Used to query the result of an execution with method calls.
/// </summary>
public struct ExitStatus
{
    public int Code { get; }
    private bool _timeOut;

    /// <summary>
    ///  Default constructor when the execution finished.
    /// </summary>
    /// <param name="code">The exit code</param>
    public ExitStatus(int code)
    {
        this.Code = code;
        this._timeOut = false;
    }

    /// <summary>
    ///  Default constructor when the execution potentially timed out.
    /// </summary>
    /// <param name="code">The exit code</param>
    /// <param name="timeOut">True if the execution timed out</param>
    public ExitStatus(int code, bool timeOut)
    {
        this.Code = code;
        this._timeOut = timeOut;
    }

    /// <summary>
    ///  Flag signalling that the execution timed out.
    /// </summary>
    public bool DidTimeOut { get { return _timeOut; } }

    /// <summary>
    ///  Implicit conversion from ExitStatus to the exit code.
    /// </summary>
    /// <param name="exitStatus">The exit status</param>
    /// <returns>The exit code</returns>
    public static implicit operator int(ExitStatus exitStatus)
    {
        return exitStatus.Code;
    }

    /// <summary>
    ///  Trigger Exception for non-zero exit code.
    /// </summary>
    /// <param name="errorMessage">The message to use in the Exception</param>
    /// <returns>The exit status for further queries</returns>
    public ExitStatus ExceptionOnError(string errorMessage)
    {
        if (this.Code != 0)
        {
            throw new Exception(errorMessage);
        }

        return this;
    }
}

public static class PathHelper
{
    public static string Combine(params string[] paths) =>
        System.IO.Path.Combine(paths);

    public static string GetDirectoryName(string path) =>
        System.IO.Path.GetDirectoryName(path);

    public static string GetFileName(string path) =>
        System.IO.Path.GetFileName(path);

    public static string GetFullPath(string path) =>
        System.IO.Path.GetFullPath(path);
}

string CombinePaths(params string[] paths)
{
    return PathHelper.Combine(paths);
}