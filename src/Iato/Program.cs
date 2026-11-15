using System.Reflection;

namespace Iato;

internal sealed class UsageException(string message) : Exception(message);

internal static class Program
{
    private const string Usage = """
        iato: Entra ID sign in for Debian through the SSSD idp provider

        Usage:
          iato register-app [--name sssd-debian-hosts] [--out entra-app.env]
          iato render --env <file> --group <group> [--provider idp|ldap] [--out sssd.conf]
          iato check [--config /etc/sssd/sssd.conf] [--provider idp|ldap] [--host]
          iato install --env <file> --group <group> [--provider idp|ldap]
          iato verify <user> [--provider idp|ldap]
          iato version

        For idp the env file holds TENANT_ID, CLIENT_ID, CLIENT_SECRET and --group is an Entra group.
        For ldap it holds LDAP_URI, SEARCH_BASE, BIND_DN, BIND_PASSWORD and --group is a group DN.
        register-app reads a Graph token from IATO_GRAPH_TOKEN or 'az account get-access-token'.
        Files holding the client secret are written with mode 0600.
        """;

    public static async Task<int> Main(string[] argv)
    {
        try
        {
            var a = Args.Parse(argv);
            switch (a.Command)
            {
                case "register-app": return await RegisterApp(a);
                case "render": return Render(a);
                case "check": return Check(a);
                case "install": return Install(a);
                case "verify": return Verify(a);
                case "version" or "--version":
                    Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));
                    return 0;
                case null or "help" or "--help" or "-h": Console.WriteLine(Usage); return a.Command is null ? 2 : 0;
                default: throw new UsageException($"unknown command '{a.Command}'");
            }
        }
        catch (UsageException e) { Console.Error.WriteLine($"iato: {e.Message}\n\n{Usage}"); return 2; }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException)
        { Console.Error.WriteLine($"iato: {e.Message}"); return 1; }
    }

    private static async Task<int> RegisterApp(Args a)
    {
        var token = await EntraRegistrar.AcquireTokenAsync();
        using var http = new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var r = await new EntraRegistrar(http).RegisterAsync(a.Get("name", "sssd-debian-hosts"));
        var outPath = a.Get("out", "entra-app.env");
        SssdConfig.WriteSecretFile(outPath, EntraRegistrar.EnvFile(r));
        Console.WriteLine($"Registered client {r.ClientId} in tenant {r.TenantId}. Wrote {outPath} (mode 0600).");
        Console.WriteLine($"Rotate the client secret before {r.SecretExpires:yyyy-MM-dd}.");
        return 0;
    }

    private static Dictionary<string, string> Values(Args a)
    {
        var env = EntraRegistrar.ReadEnvFile(a.Require("env"));
        env["ALLOWED_GROUP"] = a.Require("group");
        return env;
    }

    private static int Render(Args a)
    {
        var text = SssdConfig.Render(Providers.Get(a.Opt("provider")), Values(a));
        var outPath = a.Opt("out");
        if (outPath is null) Console.Write(text);
        else SssdConfig.WriteSecretFile(outPath, text);
        return 0;
    }

    private static int Check(Args a)
    {
        var path = a.Get("config", "/etc/sssd/sssd.conf");
        var findings = SssdConfig.Check(File.ReadAllText(path), Providers.Get(a.Opt("provider")));
        if (a.Flag("host")) findings.AddRange(HostSystem.CheckHost(path, Providers.Get(a.Opt("provider"))));
        foreach (var f in findings) Console.WriteLine(f);
        var errors = findings.Count(f => f.Severity == Severity.Error);
        Console.WriteLine(errors == 0 ? $"{path}: ok" : $"{path}: {errors} error(s)");
        return errors == 0 ? 0 : 1;
    }

    private static int Install(Args a)
    {
        if (!HostSystem.IsRoot()) throw new InvalidOperationException("install must run as root");
        var provider = Providers.Get(a.Opt("provider"));
        var text = SssdConfig.Render(provider, Values(a));

        if (provider == Providers.Idp && HostSystem.Codename() is "bookworm" or "trixie")
        {
            Console.WriteLine("This Debian release ships SSSD older than 2.11; pinning SSSD packages from forky.");
            File.WriteAllText("/etc/apt/sources.list.d/forky.sources", Templates.Read("templates/apt/forky.sources"));
            File.WriteAllText("/etc/apt/preferences.d/forky-sssd", Templates.Read("templates/apt/forky-sssd.pref"));
        }
        HostSystem.Must("apt-get", "update");
        HostSystem.Must("apt-get", ["install", "-y", .. provider.Packages]);

        Directory.CreateDirectory("/etc/sssd");
        SssdConfig.WriteSecretFile("/etc/sssd/sssd.conf", text);
        var findings = SssdConfig.Check(text, provider);
        findings.AddRange(HostSystem.CheckHost("/etc/sssd/sssd.conf", provider));
        foreach (var f in findings) Console.WriteLine(f);
        if (findings.Any(f => f.Severity == Severity.Error)) throw new InvalidOperationException("host checks failed; SSSD not started");

        HostSystem.Must("pam-auth-update", "--enable", "sss", "mkhomedir", "--force");
        if (provider == Providers.Idp)
            File.WriteAllText("/etc/ssh/sshd_config.d/50-entra-device-code.conf", Templates.Read("templates/ssh/50-entra-device-code.conf"));
        HostSystem.Must("systemctl", "enable", "--now", "sssd");
        HostSystem.Must("systemctl", "restart", "sssd");
        if (HostSystem.Run("systemctl", "reload", "ssh").Code != 0) HostSystem.Run("systemctl", "reload", "sshd");
        Console.WriteLine("Done. Verify with: iato verify <user@tenant.onmicrosoft.com>");
        return 0;
    }

    private static int Verify(Args a)
    {
        var user = a.Positional.FirstOrDefault() ?? throw new UsageException("verify needs a user");
        var failed = 0;
        var domain = Providers.Get(a.Opt("provider")).Domain["domain/".Length..];
        foreach (var cmd in new[] { new[] { "sssctl", "domain-status", domain }, ["getent", "passwd", user], ["id", user], ["sssctl", "user-checks", "-a", "acct", user] })
        {
            var (code, output) = HostSystem.Run(cmd[0], cmd[1..]);
            Console.WriteLine($"$ {string.Join(' ', cmd)}\n{output}\n");
            if (code != 0) failed++;
        }
        Console.WriteLine(failed == 0 ? $"{user} resolves and may log in." : $"{failed} check(s) failed for {user}.");
        return failed == 0 ? 0 : 1;
    }
}

internal sealed class Args
{
    public string? Command { get; private init; }
    public List<string> Positional { get; } = [];
    private readonly Dictionary<string, string?> _opts = new(StringComparer.Ordinal);

    public static Args Parse(string[] argv)
    {
        var a = new Args { Command = argv.FirstOrDefault() };
        for (var i = 1; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) { a.Positional.Add(argv[i]); continue; }
            var key = argv[i][2..];
            var eq = key.IndexOf('=');
            if (eq >= 0) a._opts[key[..eq]] = key[(eq + 1)..];
            else if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) a._opts[key] = argv[++i];
            else a._opts[key] = null;
        }
        return a;
    }

    public string? Opt(string k) => _opts.GetValueOrDefault(k);
    public string Get(string k, string def) => Opt(k) ?? def;
    public string Require(string k) => Opt(k) ?? throw new UsageException($"--{k} is required");
    public bool Flag(string k) => _opts.ContainsKey(k);
}
