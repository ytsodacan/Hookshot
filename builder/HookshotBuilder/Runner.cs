using System.Diagnostics;
using System.IO;

namespace HookshotBuilder;

/// Where nx2/make_game.py and its Python are: a "tools" folder beside the app when packaged, or the source tree it sits in.
sealed record Tools(string Root, string Python)
{
    /// Which release these tools are (tools/version.txt, written when the release is packed); null from the source tree.
    public string? Version => File.Exists(Path.Combine(Root, "version.txt")) ? File.ReadAllText(Path.Combine(Root, "version.txt")).Trim() : null;

    public static Tools? Find()
    {
        var at = AppContext.BaseDirectory;
        var configured = Environment.GetEnvironmentVariable("HOOKSHOT_TOOLS");
        var roots = new[] { configured, Path.Combine(at, "tools") }.Where(r => !string.IsNullOrWhiteSpace(r));
        foreach (var root in roots)
            if (File.Exists(Path.Combine(root!, "nx2", "make_game.py")))
                return new Tools(root!, ChoosePython(root!));
        for (var d = new DirectoryInfo(at); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "nx2", "make_game.py")))
                return new Tools(d.FullName, ChoosePython(d.FullName));
        return null;
    }

    static string ChoosePython(string root)
    {
        var bundled = Path.Combine(root, "python", "python.exe");
        return File.Exists(bundled) ? bundled : "python";
    }
}

/// A command line for a child process: each argument quoted the way Windows programs (and Python) read them back
static class CommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
        var sb = new System.Text.StringBuilder("\"");
        for (var i = 0; i < a.Length; i++)
        {
            var slashes = 0;
            while (i < a.Length && a[i] == '\\') { slashes++; i++; }
            if (i == a.Length) sb.Append('\\', slashes * 2);
            else if (a[i] == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(a[i]);
        }
        return sb.Append('"').ToString();
    }
}

/// nx2/make_game.py as a child process: its lines as they come, and how it ended.
sealed class Runner
{
    readonly Tools tools;
    readonly List<string> args;
    Process? process;
    string? error;
    bool cancelled;

    public event Action<string>? Line;
    public event Action<string?>? Finished;

    public Runner(Tools tools, List<string> args)
    {
        this.tools = tools;
        this.args = args;
    }

    static ProcessStartInfo Info(Tools tools, IEnumerable<string> args)
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        var all = new List<string> { "-I", Path.Combine(tools.Root, "nx2", "make_game.py") };
        all.AddRange(args);
        var si = new ProcessStartInfo(tools.Python)
        {
            Arguments = CommandLine.Join(all),
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = tools.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        si.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        // (written into the game folder's hookshot_version.txt: the game starts this app again to update, host/hos_update.c)
        var self = Process.GetCurrentProcess().MainModule?.FileName;
        if (self != null) si.EnvironmentVariables["HOOKSHOT_BUILDER_EXE"] = self;
        // (the folder's own compiler, headers and libraries, when it has them: nothing of the PC's but Windows' own)
        var sets = new[] { ("EMSDK", "emsdk"), ("RX_CPM", "cpm"), ("WGPU_NATIVE", "wgpu-native") };
        foreach (var (name, folder) in sets)
        {
            var path = Path.Combine(tools.Root, folder);
            if (Directory.Exists(path)) si.EnvironmentVariables[name] = path;
        }
        return si;
    }

    public void Start()
    {
        Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(Info(tools, args))!;
                process = p;
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) Line?.Invoke(e.Data); };
                p.BeginErrorReadLine();
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                while (p.StandardOutput.ReadLine() is { } line)
                {
                    if (line.StartsWith("@@error ")) error = line.Substring(8);
                    Line?.Invoke(line);
                }
                p.WaitForExit();
                Finished?.Invoke(cancelled ? "cancelled" : p.ExitCode == 0 ? null : error ?? "The build tools stopped without saying why. See the details.");
            }
            catch (Exception e)
            {
                Finished?.Invoke("The build tools could not start: " + e.Message);
            }
        });
    }

    public void Cancel()
    {
        cancelled = true;
        // (the whole tree: python, then the compiler it started)
        try
        {
            var id = process?.Id;
            if (id != null)
                Process.Start(new ProcessStartInfo("taskkill", $"/PID {id} /T /F") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(5000);
        }
        catch (Exception) { }
        try { process?.Kill(); } catch (Exception) { }
    }

    /// Is this the supported game and version? The answer in plain words.
    public static async Task<(bool Ok, string Text)> Check(Tools tools, IEnumerable<string> args)
    {
        try
        {
            using var p = Process.Start(Info(tools, args))!;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            var result = (false, "The game files could not be read.");
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("@@check ok ")) result = (true, line.Substring(11));
                else if (line.StartsWith("@@check bad ")) result = (false, line.Substring(12));
                else if (line.StartsWith("@@error ")) result = (false, line.Substring(8));
            }
            await Task.Run(() => p.WaitForExit());
            return result;
        }
        catch (Exception)
        {
            // (the package's own Python missing: an antivirus took it, most often, after a false alarm on the download)
            if (tools.Python == "python" && Directory.Exists(Path.Combine(tools.Root, "python")))
                return (false, "The builder's own Python (tools/python/python.exe) is missing. Your antivirus probably removed it: restore it " +
                               "from quarantine, or unpack the zip again into a folder your antivirus allows, then try again.");
            return (false, "Python was not found. Install Python 3 and add it to PATH, then try again.");
        }
    }
}
