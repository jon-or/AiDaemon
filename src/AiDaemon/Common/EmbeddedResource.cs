using System.Reflection;

namespace AiDaemon.Common;

static class EmbeddedResource
{
    /// <summary>
    /// Loads an embedded resource by trailing-name match (e.g. <c>"Schema.sql"</c> resolves
    /// against <c>AiDaemon.Storage.Schema.sql</c>). Throws if the resource isn't registered as
    /// <c>&lt;EmbeddedResource&gt;</c> in the .csproj.
    /// </summary>
    public static string Load(Assembly assembly, string fileName)
    {
        using var stream = OpenStream(assembly, fileName);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>
    /// Open an embedded resource as a stream so binary assets (icons, etc.) skip the text
    /// reader. Caller is responsible for disposing.
    /// </summary>
    public static Stream OpenStream(Assembly assembly, string fileName)
    {
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Embedded resource {fileName} not found. Check AiDaemon.csproj <EmbeddedResource> entries.");

        return assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Failed to open embedded resource {name}");
    }
}
