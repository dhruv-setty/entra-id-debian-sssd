using Iato;
using Xunit;

public class SssdConfigTests
{
    private static readonly Dictionary<string, string> Values = new()
    {
        ["TENANT_ID"] = "00000000-0000-0000-0000-000000000000",
        ["CLIENT_ID"] = "11111111-1111-1111-1111-111111111111",
        ["CLIENT_SECRET"] = "placeholder",
        ["ALLOWED_GROUP"] = "linux-admins",
    };

    [Fact]
    public void RenderedTemplatePassesCheck()
    {
        var text = SssdConfig.Render(Providers.Idp, Values);
        Assert.DoesNotContain("__", text);
        Assert.Empty(SssdConfig.Check(text, Providers.Idp).Where(f => f.Severity == Severity.Error));
    }

    [Fact]
    public void ClientIdMustBeGuid()
    {
        var v = new Dictionary<string, string>(Values) { ["CLIENT_ID"] = "not-a-guid" };
        Assert.Contains(SssdConfig.Check(SssdConfig.Render(Providers.Idp, v), Providers.Idp), f => f.Message.Contains("GUID"));
    }

    [Fact]
    public void MissingValueIsRejected()
    {
        var v = new Dictionary<string, string>(Values);
        v.Remove("CLIENT_SECRET");
        Assert.Throws<UsageException>(() => SssdConfig.Render(Providers.Idp, v));
    }

    [Fact]
    public void NewlineInValueIsRejected()
    {
        var v = new Dictionary<string, string>(Values) { ["ALLOWED_GROUP"] = "a\nid_provider = files" };
        Assert.Throws<UsageException>(() => SssdConfig.Render(Providers.Idp, v));
    }

    [Theory]
    [InlineData("config_file_version = 2", "config_file_version")]
    [InlineData("idp_type = entra_id", "more than once")]
    public void BadSettingsAreFound(string line, string expected)
    {
        var text = SssdConfig.Render(Providers.Idp, Values);
        var section = line.StartsWith("config") ? "[sssd]" : "[domain/entra]";
        text = text.Replace(section, $"{section}\n{line}");
        Assert.Contains(SssdConfig.Check(text, Providers.Idp), f => f.Message.Contains(expected));
    }

    [Fact]
    public void SecretFileIsOwnerOnly()
    {
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        SssdConfig.WriteSecretFile(p, "x");
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(p));
        File.Delete(p);
    }

    [Theory]
    [InlineData("2.13.1-5", 2, 13, 1)]
    [InlineData("1:2.11.0~rc1-1", 2, 11, 0)]
    public void DebianVersionsParse(string v, int a, int b, int c) =>
        Assert.Equal(new Version(a, b, c), HostSystem.ParseDebianVersion(v));

    [Fact]
    public void ApplicationRequestsExpectedPermissions()
    {
        var body = EntraRegistrar.ApplicationBody("x").ToJsonString();
        Assert.Contains("df021288-bdef-4463-88db-98f22de89214", body);
        Assert.Contains("98830695-27a2-44f7-8c18-0c3ebc9698f6", body);
        Assert.Contains("\"isFallbackPublicClient\":true", body);
    }
}

public class LdapProviderTests
{
    private static readonly Dictionary<string, string> Values = new()
    {
        ["LDAP_URI"] = "ldaps://ldap.example.internal",
        ["SEARCH_BASE"] = "dc=example,dc=internal",
        ["BIND_DN"] = "cn=sssd,ou=service,dc=example,dc=internal",
        ["BIND_PASSWORD"] = "placeholder",
        ["ALLOWED_GROUP"] = "cn=linux-login,ou=groups,dc=example,dc=internal",
    };

    [Fact]
    public void RenderedLdapTemplatePassesCheck() =>
        Assert.Empty(SssdConfig.Check(SssdConfig.Render(Providers.Ldap, Values), Providers.Ldap).Where(f => f.Severity == Severity.Error));

    [Fact]
    public void PlainLdapIsRejected()
    {
        var v = new Dictionary<string, string>(Values) { ["LDAP_URI"] = "ldap://ldap.example.internal" };
        Assert.Contains(SssdConfig.Check(SssdConfig.Render(Providers.Ldap, v), Providers.Ldap), f => f.Message.Contains("ldaps://"));
    }
}
