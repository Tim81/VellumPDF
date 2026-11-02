// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #530 and #508: what a <see cref="Document"/> is left in by a save that threw, and what a failed
/// save to a path does to the file that was already there.
///
/// Before, a document behaved three ways after a failed save and two were silent. A refusal that
/// came after layout left the pages behind, so the next save laid everything out again on top of
/// them and wrote several times the pages the caller asked for, with no exception. The page counts
/// below are <c>/Type /Page</c> occurrences in the file, which is how the issue measured it.
/// </summary>
public sealed class SaveLifecycleTests
{
    private static TextStyle Style(double size = 12) =>
        new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static Document Good() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 200),
        Margins = new EdgeInsets(10),
    };

    /// <summary>The issue's own geometry: 300pt text on a 300x200pt page cannot be placed.</summary>
    private static Document TooTall()
    {
        var doc = Good();
        doc.Add(new Paragraph("Hello", Style(300)));
        return doc;
    }

    private static int Pages(byte[] pdf) =>
        Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![s\w])").Count;

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] FreshOnePage()
    {
        using var fresh = Good();
        fresh.Add(new Paragraph("Hello", Style()));
        return Save(fresh);
    }

    // ── Layout threw: the pages it appended cannot be taken back ─────────────

    [Fact]
    public void LayoutThatThrew_makesTheNextSaveRefuseInsteadOfAppendingASecondLayout()
    {
        using var doc = TooTall();
        Assert.Throws<InvalidOperationException>(() => Save(doc));

        // The caller enlarges the page and tries again, which used to give 4 pages.
        doc.PageSize = new PdfRectangle(0, 0, 600, 800);
        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Contains("cannot be saved again", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Create a new Document", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public async Task LayoutThatThrew_refusesTheAsyncSaveToo()
    {
        using var doc = TooTall();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var first = new MemoryStream();
            await doc.SaveAsync(first, TestContext.Current.CancellationToken);
        });

        using var second = new MemoryStream();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SaveAsync(second, TestContext.Current.CancellationToken));

        Assert.Contains("cannot be saved again", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, second.Length);
    }

    // ── Geometry refused before anything was laid out: still recoverable ─────

    [Fact]
    public void GeometryRefusedBeforeLayout_isStillRecoverable_andWritesTheSameFileAsAFreshDocument()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.PageSize = new PdfRectangle(0, 0, 15, 200);   // margins meet the page width
        Assert.Throws<ArgumentException>(() => Save(doc));

        doc.PageSize = new PdfRectangle(0, 0, 300, 200);
        var recovered = Save(doc);

        Assert.Equal(1, Pages(recovered));
        Assert.Equal(FreshOnePage().Length, recovered.Length);
    }

    // ── Refusals that need no layout now come before it ──────────────────────

    [Fact]
    public void NullDestination_isRefusedBeforeLayout_soTheRetryWritesOnePage()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        var ex = Assert.Throws<ArgumentNullException>(() => doc.Save((Stream)null!));
        Assert.Equal("destination", ex.ParamName);

        Assert.Equal(1, Pages(Save(doc)));    // used to be 2
    }

    [Fact]
    public void UnwritableStream_isRefusedBeforeLayout_andDoesNotBurnTheDocument()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        using var readOnly = new MemoryStream(new byte[8], writable: false);

        var ex = Assert.Throws<ArgumentException>(() => doc.Save(readOnly));
        Assert.Equal("destination", ex.ParamName);

        // Used to throw "This document has already been written".
        Assert.Equal(1, Pages(Save(doc)));
    }

    [Fact]
    public void ObjectStreamsWithEncryption_isRefusedBeforeLayout_soCorrectingItWritesOnePage()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.UseObjectStreams = true;
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });

        Assert.Throws<NotSupportedException>(() => Save(doc));

        doc.UseObjectStreams = false;
        Assert.Equal(1, Pages(Save(doc)));    // used to be 2
    }

    [Fact]
    public void PdfAWithEncryption_isRefusedBeforeLayout_soCorrectingItWritesOnePage()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });

        Assert.Throws<InvalidOperationException>(() => Save(doc));

        doc.Conformance = PdfConformance.None;
        Assert.Equal(1, Pages(Save(doc)));
    }

    [Fact]
    public async Task AsyncSave_refusesTheSameThingsBeforeLayout()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        using var readOnly = new MemoryStream(new byte[8], writable: false);

        await Assert.ThrowsAsync<ArgumentException>(() => doc.SaveAsync(readOnly, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => doc.SaveAsync((Stream)null!, TestContext.Current.CancellationToken));

        using var ok = new MemoryStream();
        await doc.SaveAsync(ok, TestContext.Current.CancellationToken);
        Assert.Equal(1, Pages(ok.ToArray()));
    }

    // ── Unchanged: a document that was written is still written once ─────────

    [Fact]
    public void SavingTwice_stillThrowsTheAlreadyWrittenMessage()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        Save(doc);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("already been written", ex.Message, StringComparison.Ordinal);
    }

    // ── #508: a failed save to a path leaves the file that was there ─────────

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vellumpdf-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string[] Leftovers() =>
            Directory.GetFiles(Path, ".vellumpdf-*.tmp");

        public void Dispose()
        {
            foreach (var f in Directory.GetFiles(Path))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
    }

    [Fact]
    public void FailedSaveToAPath_leavesThePreviousFileByteForByte_andNoTemporaryFile()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "report.pdf");
        using (var good = Good())
        {
            good.Add(new Paragraph("Hello", Style()));
            good.Save(target);
        }
        var before = File.ReadAllBytes(target);
        Assert.NotEmpty(before);

        using var bad = TooTall();
        Assert.Throws<InvalidOperationException>(() => bad.Save(target));

        Assert.Equal(before, File.ReadAllBytes(target));   // used to be zero bytes
        Assert.Empty(dir.Leftovers());
    }

    [Fact]
    public async Task FailedAsyncSaveToAPath_leavesThePreviousFileByteForByte_andNoTemporaryFile()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "report.pdf");
        using (var good = Good())
        {
            good.Add(new Paragraph("Hello", Style()));
            await good.SaveAsync(target, TestContext.Current.CancellationToken);
        }
        var before = await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken);

        using var bad = TooTall();
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.SaveAsync(target, TestContext.Current.CancellationToken));

        Assert.Equal(before, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.Empty(dir.Leftovers());
    }

    [Fact]
    public void FailedSaveToANewPath_createsNothing()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "never.pdf");

        using var bad = TooTall();
        Assert.Throws<InvalidOperationException>(() => bad.Save(target));

        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void SuccessfulSaveToAPath_replacesTheFile_andLeavesNoTemporaryFile()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "report.pdf");
        File.WriteAllText(target, "old contents");

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.Save(target);

        var written = File.ReadAllBytes(target);
        Assert.StartsWith("%PDF-", System.Text.Encoding.Latin1.GetString(written), StringComparison.Ordinal);
        Assert.Equal(1, Pages(written));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task SuccessfulAsyncSaveToAPath_replacesTheFile()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "report.pdf");
        await File.WriteAllTextAsync(target, "old contents", TestContext.Current.CancellationToken);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        await doc.SaveAsync(target, TestContext.Current.CancellationToken);

        Assert.Equal(1, Pages(await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken)));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void SaveToAPath_stillRefusesTheTargetsItAlwaysRefused()
    {
        using var dir = new TempDir();
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        Assert.Throws<ArgumentNullException>(() => doc.Save((string)null!));
        Assert.Throws<ArgumentException>(() => doc.Save(string.Empty));
        Assert.Throws<UnauthorizedAccessException>(() => doc.Save(dir.Path));                  // a directory
        Assert.Throws<DirectoryNotFoundException>(() => doc.Save(Path.Combine(dir.Path, "missing", "x.pdf")));

        var readOnly = Path.Combine(dir.Path, "ro.pdf");
        File.WriteAllText(readOnly, "keep");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        Assert.Throws<UnauthorizedAccessException>(() => doc.Save(readOnly));
        Assert.Equal("keep", File.ReadAllText(readOnly));

        // None of those refusals touched the document, so it still saves.
        var ok = Path.Combine(dir.Path, "ok.pdf");
        doc.Save(ok);
        Assert.Equal(1, Pages(File.ReadAllBytes(ok)));
    }
}
