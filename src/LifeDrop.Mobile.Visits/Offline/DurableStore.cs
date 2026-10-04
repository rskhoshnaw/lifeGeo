namespace LifeDrop.Mobile.Offline;

// Where an operation stands (plan step 1.14, PRD §7). The four states are shown separately to the user.
public enum SyncState
{
    Local,      // saved on the phone only
    Sent,       // sent, waiting for the server's answer
    Accepted,   // the server accepted it
    Rejected    // the server refused it; sending again will not help
}

// Small key/value storage that survives closing the app.
public interface IDurableStore
{
    string? Read(string key);
    void Write(string key, string value);
    void Delete(string key);
}

// One file per key in the app's private data folder. A write goes to a temporary file that then replaces the old
// one, so a crash during a write leaves the previous content intact.
public sealed class FileDurableStore : IDurableStore
{
    private readonly string _directory;
    private readonly object _gate = new();

    public FileDurableStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        Directory.CreateDirectory(_directory);
    }

    public string? Read(string key)
    {
        lock (_gate)
        {
            var path = PathFor(key);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    public void Write(string key, string value)
    {
        lock (_gate)
        {
            var path = PathFor(key);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, value);
            File.Move(temporary, path, overwrite: true);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var path = PathFor(key);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private string PathFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Keys are letters, digits, '-' and '_' only.", nameof(key));
        return Path.Combine(_directory, key + ".json");
    }
}
