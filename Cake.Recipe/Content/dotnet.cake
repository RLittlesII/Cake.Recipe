/*
What are we trying to accompish

Detect current runtime env (Windows, Linux, OSX)
Call appropriate command shell (bash, powershell)
Build an argument string based on shell type
Accept incoming install script (default to a specific version)
Install in either local or global (depending on user designation)
*/

BuildParameters.Tasks.InstallDotNetCoreTask = Task("InstallDotNetCoreTask")
    .WithCriteria(ToolSettings.InstallDotNetSdkVersion != null)
    .Does((context) =>
        {
            // Gets the dotnet install variables required to install the tool.
            var dotnetInstall = DotNetInstall.Load(context);
        });


void InstallDotNetSdk(BuildEnvironment env, DotNetInstall plan, string version, string installFolder = "./.donet")
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

/// <summary>
///  Class representing build.json
/// </summary>
public class DotNetInstall
{
    public DirectoryPath WorkingDirectory { get; }
    public string DotNetSdkPath { get; } = ".dotnet";
    public static string DotNetCommand { get; private set; }
    public static string ShellCommand { get; private set; }
    public static string ShellArgument { get; private set; }
    public static string ShellScriptFileExtension { get; private set; }

    public string DotNetInstallScriptURL { get; set; }
    public string DotNetChannel { get; set; }
    public string DotNetVersion { get; set; }
    public string LegacyDotNetVersion { get; set; }

    public bool IsWindows => _platform == PlatformFamily.Windows;
    public bool IsMacOS => _platform == PlatformFamily.OSX;
    public bool IsLinux => _platform == PlatformFamily.Linux;
    public bool Is32Bit => !_is64Bit;
    public bool Is64Bit => _is64Bit;
    

    private PlatformFamily _platform;
    private bool _is64Bit;

    public static DotNetInstall Load(ICakeContext context, bool useGlobalDotNetSdk = true)
    {
        return Load(context.Environment);
    }

    public static DotNetInstall Load(ICakeEnvironment environment, bool useGlobalDotNetSdk = true)
    {
        
        _platform = environment.Platform.Family;
        _is64Bit = environment.Is64BitOperativeSystem();
        
        this.WorkingDirectory = environment.WorkingDirectory;

        this.DotNetCommand = useGlobalDotNetSdk ? "dotnet" : PathHelper.Combine(this.WorkingDirectory.FullPath, ".dotnet", "dotnet");

        this.ShellCommand = this.IsWindows ? "powershell" : "bash"; 
        this.ShellArgument = this.IsWindows ? "-NoProfile /Command" : "-C";
        this.ShellScriptFileExtension = this.IsWindows ? "ps1" : "sh";
        
        var buildJsonPath = PathHelper.Combine(environment.WorkingDirectory, "build.json");
        return JsonConvert.DeserializeObject<DotNetInstall>(System.IO.File.ReadAllText(buildJsonPath));
    }

    public static DotNetInstall DotNetInstall(ICakeEnvironment environment)
    {
        var buildJsonPath = PathHelper.Combine(environment.WorkingDirectory, "build.json");
        return JsonConvert.DeserializeObject<DotNetInstall>(System.IO.File.ReadAllText(buildJsonPath));
    }

    private DotNetInstall Initialize(DotNetInstall dotnet)
    {
        DotNetInstallScriptURL = dotnet.DotNetInstallScriptURL;
        DotNetChannel = dotnet.DotNetChannel;
        DotNetVersion = dotnet.DotNetVersion;
        LegacyDotNetVersion = dotnet.LegacyDotNetVersion;
    }
}

public class InstallPlatform
{    
    public bool IsWindows => _platform == PlatformFamily.Windows;
    public bool IsMacOS => _platform == PlatformFamily.OSX;
    public bool IsLinux => _platform == PlatformFamily.Linux;

    public bool Is32Bit => !_is64Bit;
    public bool Is64Bit => _is64Bit;

    private PlatformFamily _platform;
    private bool _is64Bit;

    public InstallPlatform(ICakeEnvironment environment)
    {
        _platform = environment.Platform.Family;
        _is64Bit = environment.Is64BitOperativeSystem();
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