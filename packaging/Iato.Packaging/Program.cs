// Builds dist/: iato_<v>_{amd64,arm64}.deb, iato-linux-{x64,arm64}, iato-<v>.tar.gz and SHA256SUMS.
// Usage: dotnet run --project packaging/Iato.Packaging
using System.Diagnostics;
using System.Security.Cryptography;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

var root = FindRoot();
var version = File.ReadAllText(Path.Combine(root, "VERSION")).Trim();
var dist = Path.Combine(root, "dist");
if (Directory.Exists(dist)) Directory.Delete(dist, recursive: true);
Directory.CreateDirectory(dist);

Run("dotnet", "test", "-c", "Release");

foreach (var (rid, arch) in new[] { ("linux-x64", "amd64"), ("linux-arm64", "arm64") })
{
    var pub = Temp();
    Run("dotnet", "publish", "src/Iato", "-c", "Release", "-r", rid, "--self-contained",
        "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=none", "-p:ContinuousIntegrationBuild=true", "-o", pub);
    var binary = Path.Combine(pub, "iato");
    Install(binary, Path.Combine(dist, $"iato-{rid}"), Exec);

    var stage = Temp();
    Install(binary, Path.Combine(stage, "usr/bin/iato"), Exec);
    Install(Path.Combine(root, "README.md"), Path.Combine(stage, "usr/share/doc/iato/README.md"), Read);
    Install(Path.Combine(root, "LICENSE"), Path.Combine(stage, "usr/share/doc/iato/copyright"), Read);
    var control = File.ReadAllText(Path.Combine(root, "packaging/control.in"))
        .Replace("@VERSION@", version).Replace("@ARCH@", arch);
    Directory.CreateDirectory(Path.Combine(stage, "DEBIAN"));
    File.WriteAllText(Path.Combine(stage, "DEBIAN/control"), control);
    foreach (var d in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories).Append(stage))
        File.SetUnixFileMode(d, Exec);
    Run("dpkg-deb", "--root-owner-group", "-Zxz", "--build", stage, Path.Combine(dist, $"iato_{version}_{arch}.deb"));
    Directory.Delete(stage, true);
    Directory.Delete(pub, true);
}

Run("git", "archive", "--format=tar.gz", $"--prefix=iato-{version}/", "-o", Path.Combine(dist, $"iato-{version}.tar.gz"), "HEAD");

var sums = Directory.GetFiles(dist).Order(StringComparer.Ordinal).ToList()
    .Select(f => $"{Sha256(f)}  ./{Path.GetFileName(f)}").ToList();
File.WriteAllLines(Path.Combine(dist, "SHA256SUMS"), sums);
foreach (var f in Directory.EnumerateFiles(dist).Order(StringComparer.Ordinal))
    Console.WriteLine($"{new FileInfo(f).Length,12}  {Path.GetFileName(f)}");
return 0;

void Run(string file, params string[] args)
{
    Console.Error.WriteLine($"+ {file} {string.Join(' ', args)}");
    var psi = new ProcessStartInfo(file) { WorkingDirectory = root };
    foreach (var a in args) psi.ArgumentList.Add(a);
    psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    psi.Environment["DOTNET_NOLOGO"] = "1";
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException($"{file} exited {p.ExitCode}");
}

static void Install(string src, string dst, UnixFileMode mode)
{
    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
    File.Copy(src, dst, overwrite: true);
    File.SetUnixFileMode(dst, mode);
}

static string Sha256(string path)
{
    using var s = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
}

static string Temp() => Directory.CreateTempSubdirectory("iato-pkg-").FullName;

static string FindRoot()
{
    for (var d = new DirectoryInfo(Environment.CurrentDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "VERSION")) && Directory.Exists(Path.Combine(d.FullName, "src/Iato")))
            return d.FullName;
    throw new InvalidOperationException("run inside the repository");
}

partial class Program
{
    const UnixFileMode Read = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    const UnixFileMode Exec = Read | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
}
