namespace HadramoutNET.Core.Exporting;

/// <summary>Atomically publish fully generated exports while retaining old data on failures.</summary>
internal static class AtomicExportFile
{
    public static string Write(string path, Action<string> writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writer);
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string staged = Path.Combine(directory, ".nidi-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            writer(staged);
            using (var stream = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(flushToDisk: true);
            File.Move(staged, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(staged))
                File.Delete(staged);
        }
    }
}
