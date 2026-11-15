namespace Iato;

/// <summary>An SSSD domain layout the CLI can render and check.</summary>
internal sealed record Provider(
    string Name,
    string Domain,
    string Template,
    string[] RequiredKeys,
    string[] Placeholders,
    string[] Packages,
    string BackendLibrary,
    Version MinSssd);

internal static class Providers
{
    public static readonly Provider Idp = new(
        Name: "idp",
        Domain: "domain/entra",
        Template: "templates/sssd/sssd.conf",
        RequiredKeys:
        [
            "id_provider", "auth_provider", "idp_type", "idp_client_id", "idp_client_secret",
            "idp_token_endpoint", "idp_device_auth_endpoint", "idp_userinfo_endpoint",
            "idp_id_scope", "idp_auth_scope", "simple_allow_groups",
            "idmap_range_min", "idmap_range_max", "idmap_range_size",
        ],
        Placeholders: ["TENANT_ID", "CLIENT_ID", "CLIENT_SECRET", "ALLOWED_GROUP"],
        Packages: ["sssd", "sssd-idp", "libnss-sss", "libpam-sss", "sssd-tools"],
        BackendLibrary: "libsss_idp.so",
        MinSssd: new(2, 11, 0));

    public static readonly Provider Ldap = new(
        Name: "ldap",
        Domain: "domain/openldap",
        Template: "templates/sssd/sssd-ldap.conf",
        RequiredKeys:
        [
            "id_provider", "auth_provider", "access_provider", "ldap_uri", "ldap_search_base",
            "ldap_default_bind_dn", "ldap_default_authtok", "ldap_tls_reqcert", "ldap_access_filter",
        ],
        Placeholders: ["LDAP_URI", "SEARCH_BASE", "BIND_DN", "BIND_PASSWORD", "ALLOWED_GROUP"],
        Packages: ["sssd", "sssd-ldap", "libnss-sss", "libpam-sss", "sssd-tools"],
        BackendLibrary: "libsss_ldap.so",
        MinSssd: new(2, 8, 0));

    public static readonly Dictionary<string, Provider> All = new(StringComparer.OrdinalIgnoreCase)
    {
        [Idp.Name] = Idp,
        [Ldap.Name] = Ldap,
    };

    public static Provider Get(string? name) =>
        All.TryGetValue(name ?? Idp.Name, out var p)
            ? p
            : throw new UsageException($"unknown provider '{name}'; known: {string.Join(", ", All.Keys)}");
}
