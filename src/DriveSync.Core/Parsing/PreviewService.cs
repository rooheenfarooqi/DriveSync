using System.Diagnostics;
using DriveSync.Core.Indexing;

namespace DriveSync.Core.Parsing;

public enum PreviewState
{
    Ok,
    Offline,       // the drive isn't connected
    Locked,        // another app has the file open
    Image,         // no text to summarize
    Unsupported,   // DriveSync can't read inside this type
    TooLarge,      // bigger than the "Largest file to summarize" setting
    Missing,       // deleted since it was indexed
    Error,         // damaged, encrypted…
}

/// <summary>Everything the preview's Summary and Outline tabs show for one file.</summary>
public sealed class PreviewResult
{
    public PreviewState State { get; init; }
    public Summary? Summary { get; init; }
    public List<OutlineItem> Outline { get; init; } = new();
    public string? LockedBy { get; init; }
    public string? Message { get; init; }
    public double Milliseconds { get; init; }
}

/// <summary>
/// Reads and summarizes a file only when the user selects it, and remembers the last 200 results
/// (keyed by path, modified time, size and summary length) so going back to a file is instant.
/// </summary>
public static class PreviewService
{
    private const int CacheSize = 200;
    private static readonly Dictionary<string, LinkedListNode<(string Key, PreviewResult Result)>> Cache = new();
    private static readonly LinkedList<(string Key, PreviewResult Result)> Lru = new();

    /// <summary>
    /// Checks the things that don't need the file opened: offline drive, images, unsupported types,
    /// the size limit. Returns null when the file can be parsed.
    /// </summary>
    public static PreviewResult? Blocker(FileView file, bool offline, int maxParseMb, bool force)
    {
        if (offline) return new PreviewResult { State = PreviewState.Offline };
        if (file.Kind == FileKind.Image) return new PreviewResult { State = PreviewState.Image };
        if (!TextReaders.CanRead(file.Extension, file.Kind)) return new PreviewResult { State = PreviewState.Unsupported };
        if (!force && file.Size > (long)maxParseMb * 1024 * 1024) return new PreviewResult { State = PreviewState.TooLarge };
        return null;
    }

    public static PreviewResult? Cached(FileView file, int sentences)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(Key(file, sentences), out var node)) return null;
            Lru.Remove(node);
            Lru.AddFirst(node);
            return node.Value.Result;
        }
    }

    public static void ClearCache()
    {
        lock (Cache)
        {
            Cache.Clear();
            Lru.Clear();
        }
    }

    /// <summary>Opens, parses and summarizes the file. Runs on a background thread.</summary>
    public static PreviewResult Analyze(FileView file, int sentences, CancellationToken ct)
    {
        if (Cached(file, sentences) is { } hit) return hit;
        var clock = Stopwatch.StartNew();
        string path = file.FullPath;
        PreviewResult result;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            ParsedDocument doc = TextReaders.Read(stream, file.Extension, file.Kind, ct);
            Summary summary = ExtractiveSummarizer.Summarize(doc, sentences);
            result = new PreviewResult
            {
                State = PreviewState.Ok,
                Summary = summary,
                Outline = doc.Outline,
                Milliseconds = clock.Elapsed.TotalMilliseconds,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return new PreviewResult { State = PreviewState.Missing };
        }
        catch (DirectoryNotFoundException)
        {
            return new PreviewResult { State = PreviewState.Missing };
        }
        catch (IOException ex) when (FileLockInfo.IsLockViolation(ex))
        {
            List<string> apps = FileLockInfo.WhoIsLocking(path);
            return new PreviewResult { State = PreviewState.Locked, LockedBy = apps.Count > 0 ? string.Join(", ", apps) : null };
        }
        catch (UnauthorizedAccessException)
        {
            return new PreviewResult { State = PreviewState.Error, Message = "Windows doesn't allow DriveSync to read this file." };
        }
        catch (Exception ex)
        {
            string message = ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex.GetType().Name.Contains("Encrypt", StringComparison.Ordinal)
                ? "This file is password-protected, so its text can't be read."
                : "The file looks damaged or uses a format DriveSync can't read.";
            return new PreviewResult { State = PreviewState.Error, Message = message };
        }

        lock (Cache)
        {
            string key = Key(file, sentences);
            if (Cache.TryGetValue(key, out var old)) Lru.Remove(old);
            Cache[key] = Lru.AddFirst((key, result));
            while (Lru.Count > CacheSize)
            {
                Cache.Remove(Lru.Last!.Value.Key);
                Lru.RemoveLast();
            }
        }
        return result;
    }

    /// <summary>
    /// Loads and JIT-compiles the Word, PowerPoint, Excel and PDF readers on a background thread at start-up,
    /// so the first preview the user opens doesn't pay a one-time delay of a second or more.
    /// </summary>
    public static void WarmUp()
    {
        try
        {
            using (var ms = new MemoryStream())
            {
                using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
                {
                    var main = doc.AddMainDocumentPart();
                    main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(
                            new DocumentFormat.OpenXml.Wordprocessing.Text("DriveSync warms up its readers with this sentence.")))));
                }
                ms.Position = 0;
                ExtractiveSummarizer.Summarize(TextReaders.Read(ms, "docx", FileKind.Doc, CancellationToken.None), 3);
            }
            var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            page.AddText("DriveSync warms up its PDF reader with this sentence.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
            using var pdf = new MemoryStream(builder.Build());
            ExtractiveSummarizer.Summarize(TextReaders.Read(pdf, "pdf", FileKind.Pdf, CancellationToken.None), 3);
        }
        catch (Exception)
        {
            // Only a speed-up; nothing depends on it.
        }
    }

    private static string Key(FileView f, int sentences) => $"{f.FullPath}|{f.Modified.Ticks}|{f.Size}|{sentences}";
}
