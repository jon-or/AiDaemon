namespace AiDaemon.Io;

/// <summary>
/// Thin abstraction over <see cref="System.IO"/> calls used by the daemon: directory
/// existence, enumeration, and text reads. Anything that interacts with the worktree
/// filesystem, the .daemon-active marker, or the per-PID claude registry goes through
/// here so tests can swap in fakes.
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);

    IEnumerable<string> EnumerateDirectories(string path, string searchPattern);

    bool FileExists(string path);

    /// <summary>
    /// Reads the file using <see cref="FileShare.ReadWrite"/> so a concurrent writer
    /// (e.g. claude updating <c>~/.claude/sessions/&lt;pid&gt;.json</c>) doesn't cause us
    /// to fail. Throws on missing file / IO error — caller decides whether to retry.
    /// </summary>
    string ReadAllText(string path);

    void WriteAllText(string path, string content);

    /// <summary>Best-effort delete: returns silently if the file is already gone.</summary>
    void DeleteFile(string path);

    DateTime GetLastWriteTimeUtc(string path);
}

public class FileSystem : IFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern)
        => Directory.EnumerateDirectories(path, searchPattern);

    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    public void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    public void DeleteFile(string path)
    {
        try { File.Delete(path); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
}
