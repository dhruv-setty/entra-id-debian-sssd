using System.Reflection;

namespace Iato;

/// <summary>Configuration templates compiled into the binary, so a host needs nothing else on disk.</summary>
internal static class Templates
{
    public static string Read(string logicalName)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"template {logicalName} is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
