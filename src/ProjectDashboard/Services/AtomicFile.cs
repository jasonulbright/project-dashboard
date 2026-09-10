using System.IO;
using System.Text;

namespace ProjectDashboard.Services;

/// <summary>
/// Whole-file writes that a reader never catches half done. The content lands in a sibling
/// temporary file and replaces the destination once it is complete: a write that fails part way
/// through would otherwise leave a truncated file where a previous one used to be, and a reader
/// cannot tell that apart from a short answer.
/// </summary>
public static class AtomicFile
{
    /// <summary>UTF-8 without a byte-order mark, matching every other file this app produces.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object PublishGate = new();

    public static async Task WriteAllTextAsync(string path, string text, CancellationToken ct = default)
    {
        var staged = path + $".{Guid.NewGuid():N}.tmp";
        var created = false;
        try
        {
            await using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await using var writer = new StreamWriter(stream, Utf8NoBom);
                await writer.WriteAsync(text.AsMemory(), ct);
                await writer.FlushAsync(ct);
            }
            // Concurrent Windows replacement renames can deny each other's destination access.
            lock (PublishGate)
            {
                ct.ThrowIfCancellationRequested();
                File.Move(staged, path, overwrite: true);
            }
        }
        catch
        {
            try { if (created) File.Delete(staged); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
