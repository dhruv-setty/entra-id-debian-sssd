using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Iato;

/// <summary>
/// Creates the Entra ID app registration SSSD signs in with, through Microsoft Graph:
/// application permissions for directory lookups, delegated scopes for the device code login,
/// admin consent for both, and a one year client secret.
/// </summary>
internal sealed class EntraRegistrar(HttpClient http)
{
    public const string GraphAppId = "00000003-0000-0000-c000-000000000000";

    // Microsoft Graph permission IDs (stable across tenants).
    public static readonly (string Id, string Name)[] AppRoles =
    [
        ("df021288-bdef-4463-88db-98f22de89214", "User.Read.All"),
        ("98830695-27a2-44f7-8c18-0c3ebc9698f6", "GroupMember.Read.All"),
    ];
    public static readonly (string Id, string Name)[] Scopes =
    [
        ("37f7f235-527c-4136-accd-4a02d197296e", "openid"),
        ("14dad69e-099b-42c9-810b-d002981feec1", "profile"),
        ("64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0", "email"),
    ];

    public sealed record Result(string TenantId, string ClientId, string ClientSecret, DateTimeOffset SecretExpires);

    public static async Task<string> AcquireTokenAsync()
    {
        var env = Environment.GetEnvironmentVariable("IATO_GRAPH_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var psi = new ProcessStartInfo("az", "account get-access-token --resource-type ms-graph --query accessToken -o tsv")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start az");
        var token = (await p.StandardOutput.ReadToEndAsync()).Trim();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 || token.Length == 0)
            throw new InvalidOperationException("no Graph token: run 'az login' or set IATO_GRAPH_TOKEN");
        return token;
    }

    public static JsonObject ApplicationBody(string displayName) => new()
    {
        ["displayName"] = displayName,
        ["signInAudience"] = "AzureADMyOrg",
        ["isFallbackPublicClient"] = true,
        ["requiredResourceAccess"] = new JsonArray(new JsonObject
        {
            ["resourceAppId"] = GraphAppId,
            ["resourceAccess"] = new JsonArray(
                AppRoles.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["type"] = "Role" })
                .Concat(Scopes.Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["type"] = "Scope" }))
                .ToArray()),
        }),
    };

    public async Task<Result> RegisterAsync(string displayName, CancellationToken ct = default)
    {
        var org = await SendAsync(HttpMethod.Get, "organization?$select=id", null, ct);
        var tenantId = (string)org["value"]![0]!["id"]!;

        var app = await SendAsync(HttpMethod.Post, "applications", ApplicationBody(displayName), ct);
        var appObjectId = (string)app["id"]!;
        var clientId = (string)app["appId"]!;

        var sp = await SendAsync(HttpMethod.Post, "servicePrincipals", new JsonObject { ["appId"] = clientId }, ct);
        var spId = (string)sp["id"]!;
        var graph = await SendAsync(HttpMethod.Get, $"servicePrincipals(appId='{GraphAppId}')?$select=id", null, ct);
        var graphSpId = (string)graph["id"]!;

        foreach (var (roleId, _) in AppRoles)
            await SendAsync(HttpMethod.Post, $"servicePrincipals/{graphSpId}/appRoleAssignedTo", new JsonObject
            {
                ["principalId"] = spId, ["resourceId"] = graphSpId, ["appRoleId"] = roleId,
            }, ct);

        await SendAsync(HttpMethod.Post, "oauth2PermissionGrants", new JsonObject
        {
            ["clientId"] = spId, ["consentType"] = "AllPrincipals", ["resourceId"] = graphSpId,
            ["scope"] = string.Join(' ', Scopes.Select(s => s.Name)),
        }, ct);

        var expires = DateTimeOffset.UtcNow.AddYears(1);
        var pwd = await SendAsync(HttpMethod.Post, $"applications/{appObjectId}/addPassword", new JsonObject
        {
            ["passwordCredential"] = new JsonObject { ["displayName"] = "sssd", ["endDateTime"] = expires.ToString("o") },
        }, ct);
        return new Result(tenantId, clientId, (string)pwd["secretText"]!, expires);
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Graph {method} {path} failed: {(int)res.StatusCode} {text}");
        return JsonNode.Parse(text.Length == 0 ? "{}" : text)!;
    }

    public static string EnvFile(Result r) =>
        $"TENANT_ID={r.TenantId}\nCLIENT_ID={r.ClientId}\nCLIENT_SECRET={r.ClientSecret}\n# secret expires {r.SecretExpires:yyyy-MM-dd}\n";

    public static Dictionary<string, string> ReadEnvFile(string path) =>
        File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '#' && l.Contains('='))
            .Select(l => (k: l[..l.IndexOf('=')].Trim(), v: l[(l.IndexOf('=') + 1)..].Trim().Trim('"')))
            .ToDictionary(x => x.k, x => x.v);
}
