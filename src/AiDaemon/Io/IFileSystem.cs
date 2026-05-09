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

    string ReadAllText(string path);

    DateTime GetLastWriteTimeUtc(string path);
}

public class FileSystem : IFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern)
        => Directory.EnumerateDirectories(path, searchPattern);

    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
}
