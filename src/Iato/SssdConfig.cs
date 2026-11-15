using System.Text;
using System.Text.RegularExpressions;

namespace Iato;

internal enum Severity { Error, Warning }

internal sealed record Finding(Severity Severity, string Message)
{
    public override string ToString() => $"{(Severity == Severity.Error ? "error" : "warning")}: {Message}";
}

/// <summary>Renders and checks sssd.conf without relying on sssctl's rule set, which can lag new back ends.</summary>
internal static partial class SssdConfig
{
    [GeneratedRegex("__([A-Z_]+)__")]
    private static partial Regex PlaceholderRx();

    public static string Render(Provider provider, IReadOnlyDictionary<string, string> values)
    {
        var missing = provider.Placeholders.Where(k => !values.ContainsKey(k) || string.IsNullOrWhiteSpace(values[k])).ToList();
        if (missing.Count > 0) throw new UsageException($"missing values: {string.Join(", ", missing)}");
        foreach (var (k, v) in values)
            if (v.Contains('\n') || v.Contains('\r')) throw new UsageException($"{k} must be a single line");

        var text = PlaceholderRx().Replace(Templates.Read(provider.Template),
            m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        return text;
    }

    public static Dictionary<string, Dictionary<string, string>> Parse(string text) => Parse(text, []);

    public static Dictionary<string, Dictionary<string, string>> Parse(string text, List<string> duplicates)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        var n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                current = sections.TryGetValue(name, out var existing) ? existing : sections[name] = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null) throw new FormatException($"line {n}: expected 'key = value' inside a section");
            var key = line[..eq].Trim();
            if (current.ContainsKey(key)) duplicates.Add($"line {n}: {key} is set more than once in its section");
            current[key] = line[(eq + 1)..].Trim();
        }
        return sections;
    }

    public static List<Finding> Check(string text, Provider provider)
    {
        var f = new List<Finding>();
        Dictionary<string, Dictionary<string, string>> s;
        var duplicates = new List<string>();
        try { s = Parse(text, duplicates); }
        catch (FormatException e) { f.Add(new(Severity.Error, e.Message)); return f; }
        f.AddRange(duplicates.Select(d => new Finding(Severity.Error, d)));

        foreach (Match m in PlaceholderRx().Matches(text))
            f.Add(new(Severity.Error, $"unrendered placeholder {m.Value}"));

        if (!s.TryGetValue("sssd", out var sssd)) f.Add(new(Severity.Error, "missing [sssd] section"));
        else
        {
            if (sssd.ContainsKey("config_file_version"))
                f.Add(new(Severity.Error, "config_file_version is rejected by SSSD 2.13; remove it"));
            var domains = sssd.GetValueOrDefault("domains", "").Split(',', StringSplitOptions.TrimEntries);
            if (!domains.Contains(provider.Domain["domain/".Length..]))
                f.Add(new(Severity.Error, $"[sssd] domains does not list {provider.Domain["domain/".Length..]}"));
        }

        if (!s.TryGetValue(provider.Domain, out var d)) { f.Add(new(Severity.Error, $"missing [{provider.Domain}] section")); return f; }
        foreach (var k in provider.RequiredKeys)
            if (!d.TryGetValue(k, out var v) || v.Length == 0) f.Add(new(Severity.Error, $"[{provider.Domain}] {k} is not set"));

        if (provider == Providers.Idp)
        {
            if (d.GetValueOrDefault("id_provider") != "idp") f.Add(new(Severity.Error, "id_provider must be idp"));
            if (d.GetValueOrDefault("idp_type") != "entra_id") f.Add(new(Severity.Error, "idp_type must be entra_id"));
            foreach (var k in new[] { "idp_token_endpoint", "idp_device_auth_endpoint", "idp_userinfo_endpoint" })
                if (d.TryGetValue(k, out var url) && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps))
                    f.Add(new(Severity.Error, $"{k} must be an https URL"));
            if (d.TryGetValue("idp_client_id", out var cid) && !Guid.TryParse(cid, out _))
                f.Add(new(Severity.Error, "idp_client_id is not a GUID"));
        }

        if (provider == Providers.Ldap)
        {
            if (!d.GetValueOrDefault("ldap_uri", "").Split(',', StringSplitOptions.TrimEntries).All(u => u.StartsWith("ldaps://")))
                f.Add(new(Severity.Error, "ldap_uri must use ldaps:// so the bind password is never sent in clear"));
            if (d.GetValueOrDefault("ldap_tls_reqcert") != "demand")
                f.Add(new(Severity.Error, "ldap_tls_reqcert must be demand"));
        }

        if (long.TryParse(d.GetValueOrDefault("idmap_range_min"), out var min)
            && long.TryParse(d.GetValueOrDefault("idmap_range_max"), out var max)
            && long.TryParse(d.GetValueOrDefault("idmap_range_size"), out var size))
        {
            if (min < 1000 || max <= min || size <= 0) f.Add(new(Severity.Error, "idmap range is empty or overlaps system IDs"));
            else if ((max - min) % size != 0) f.Add(new(Severity.Warning, "idmap range is not a whole number of slices"));
        }

        if (provider == Providers.Idp && d.GetValueOrDefault("cache_credentials") == "true")
            f.Add(new(Severity.Warning, "cache_credentials = true allows offline logins after Entra access is revoked"));
        return f;
    }

    /// <summary>Writes a file that holds a secret: root-only, created with mode 0600 before any byte lands.</summary>
    public static void WriteSecretFile(string path, string content)
    {
        var tmp = path + ".tmp";
        var opts = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, opts)) fs.Write(Encoding.UTF8.GetBytes(content));
        File.Move(tmp, path, overwrite: true);
    }
}
