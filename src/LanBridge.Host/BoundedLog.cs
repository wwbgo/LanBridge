using System.Text;

namespace LanBridge.Host;

public sealed class BoundedLog
{
    private readonly string path;
    private readonly object gate = new();
    public const int MaximumFileBytes = 1024 * 1024;
    public BoundedLog(string path) { this.path = path; }
    public void Write(string message)
    {
        // Callers pass fixed events / exception TYPE only: never a request body or endpoint credentials.
        string line = DateTimeOffset.UtcNow.ToString("O") + " " + message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine;
        if (line.Length > 1024) line = line[..1024] + Environment.NewLine;
        lock (gate)
        {
            if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaximumFileBytes)
                File.Move(path, path + ".1", true);
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
    }
}
