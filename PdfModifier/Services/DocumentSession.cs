using PdfModifier.Core.Operations;

namespace PdfModifier.Services;

/// <summary>
/// The document being worked on. Every operation produces a new byte array; earlier states are parked in temporary files
/// so undo history does not pile up in memory.
/// </summary>
public sealed class DocumentSession : IDisposable
{
    private const int MaxHistory = 30;

    private readonly string _historyFolder = Path.Combine(Path.GetTempPath(), "PdfModifier", Guid.NewGuid().ToString("N"));
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private int _counter;

    public byte[]? Bytes { get; private set; }
    public PdfView? View { get; private set; }
    public string? FilePath { get; private set; }
    public bool IsDirty { get; private set; }
    public bool HasDocument => Bytes != null;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Counts the documents opened, so a change of document can be told from a change to the document.</summary>
    public int Generation { get; private set; }

    /// <summary>Raised after the document was replaced, on the thread that made the change.</summary>
    public event Action? Changed;

    public void Open(byte[] bytes, string? path)
    {
        ClearHistory();
        Replace(bytes);
        Generation++;
        FilePath = path;
        IsDirty = path == null;
        Changed?.Invoke();
    }

    /// <summary>Makes <paramref name="bytes"/> the current document and keeps the previous state for undo.</summary>
    public void Commit(byte[] bytes)
    {
        if (Bytes == null) throw new InvalidOperationException("No document is open.");
        if (ReferenceEquals(bytes, Bytes)) return;
        Park(_undo, Bytes);
        Discard(_redo);
        Replace(bytes);
        IsDirty = true;
        Changed?.Invoke();
    }

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    public void MarkSaved(string path)
    {
        FilePath = path;
        IsDirty = false;
        Changed?.Invoke();
    }

    private void Step(List<string> from, List<string> to)
    {
        if (from.Count == 0 || Bytes == null) return;
        var file = from[^1];
        from.RemoveAt(from.Count - 1);
        var restored = File.ReadAllBytes(file);
        File.Delete(file);
        Park(to, Bytes);
        Replace(restored);
        IsDirty = true;
        Changed?.Invoke();
    }

    private void Replace(byte[] bytes)
    {
        // Open the new document first: if it turns out to be unreadable the session keeps the old one.
        var view = new PdfView(bytes);
        View?.Dispose();
        View = view;
        Bytes = bytes;
    }

    private void Park(List<string> stack, byte[] bytes)
    {
        Directory.CreateDirectory(_historyFolder);
        var file = Path.Combine(_historyFolder, $"{++_counter:D6}.pdf");
        File.WriteAllBytes(file, bytes);
        stack.Add(file);
        if (stack.Count <= MaxHistory) return;
        File.Delete(stack[0]);
        stack.RemoveAt(0);
    }

    private static void Discard(List<string> stack)
    {
        foreach (var file in stack) File.Delete(file);
        stack.Clear();
    }

    private void ClearHistory()
    {
        Discard(_undo);
        Discard(_redo);
    }

    public void Dispose()
    {
        View?.Dispose();
        View = null;
        try
        {
            if (Directory.Exists(_historyFolder)) Directory.Delete(_historyFolder, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leftover temp files are cleaned up by the system eventually.
        }
    }
}
