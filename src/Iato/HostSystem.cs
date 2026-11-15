using System.Diagnostics;

namespace Iato;

/// <summary>The Debian host: package versions, the SSSD back end on disk, and the commands that change it.</summary>
internal static class HostSystem
{
    public static (int Code, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o.Trim());
        }
        catch (System.ComponentModel.Win32Exception) { return (127, $"{file}: not found"); }
    }

    public static void Must(string file, params string[] args)
    {
        Console.Error.WriteLine($"+ {file} {string.Join(' ', args)}");
        var (code, output) = Run(file, args);
        if (code != 0) throw new InvalidOperationException($"{file} exited {code}: {output}");
    }

    public static Version? SssdVersion()
    {
        var (code, v) = Run("dpkg-query", "-W", "-f=${Version}", "sssd-common");
        if (code != 0) return null;
        return ParseDebianVersion(v);
    }

    public static Version? ParseDebianVersion(string v)
    {
        var upstream = v.Split(':')[^1].Split('-')[0].Split('~')[0];
        return Version.TryParse(upstream, out var ver) ? ver : null;
    }

    public static string? Backend(string library) =>
        Directory.Exists("/usr/lib")
            ? Directory.EnumerateFiles("/usr/lib", library, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }).FirstOrDefault()
            : null;

    public static string? Codename()
    {
        const string f = "/etc/os-release";
        if (!File.Exists(f)) return null;
        return File.ReadAllLines(f).FirstOrDefault(l => l.StartsWith("VERSION_CODENAME="))?.Split('=')[1].Trim('"');
    }

    public static List<Finding> CheckHost(string confPath, Provider provider)
    {
        var f = new List<Finding>();
        var v = SssdVersion();
        if (v is null) f.Add(new(Severity.Error, "sssd-common is not installed"));
        else if (v < provider.MinSssd) f.Add(new(Severity.Error, $"SSSD {v} is too old for the {provider.Name} provider; {provider.MinSssd} or later is required"));
        if (Backend(provider.BackendLibrary) is null) f.Add(new(Severity.Error, $"{provider.BackendLibrary} not found; install {provider.Packages[1]}"));
        if (!OperatingSystem.IsWindows() && File.Exists(confPath))
        {
            var mode = File.GetUnixFileMode(confPath);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                f.Add(new(Severity.Error, $"{confPath} must be mode 0600; SSSD refuses group or world readable config"));
        }
        return f;
    }

    public static bool IsRoot() => !OperatingSystem.IsWindows() && Environment.UserName == "root";
}
