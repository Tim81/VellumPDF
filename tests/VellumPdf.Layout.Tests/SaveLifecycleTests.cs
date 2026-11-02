// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Signing;

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

        public void Dispose()
        {
            foreach (var f in Directory.GetFiles(Path))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
    }

    [Fact]
    public void FailedSaveToAPath_leavesThePreviousFileByteForByte()
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
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task FailedAsyncSaveToAPath_leavesThePreviousFileByteForByte()
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
        Assert.Single(Directory.GetFiles(dir.Path));
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
    public void SuccessfulSaveToAPath_replacesTheFile_andLeavesNothingElseBeside()
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

    [Fact]
    public async Task FailedAsyncSaveToANewPath_createsNothing()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "never.pdf");

        using var bad = TooTall();
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.SaveAsync(target, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void SaveToAPath_emptiesALongerPreviousFile_beforeCopying()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "report.pdf");
        File.WriteAllBytes(target, new byte[200_000]);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.Save(target);

        var written = File.ReadAllBytes(target);
        Assert.True(written.Length < 200_000);
        Assert.StartsWith("%PDF-", System.Text.Encoding.Latin1.GetString(written), StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", System.Text.Encoding.Latin1.GetString(written), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncSaveToAPath_emptiesALongerPreviousFile_beforeCopying()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "report.pdf");
        await File.WriteAllBytesAsync(target, new byte[200_000], TestContext.Current.CancellationToken);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        await doc.SaveAsync(target, TestContext.Current.CancellationToken);

        var written = await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken);
        Assert.True(written.Length < 200_000);
        Assert.Equal(1, Pages(written));
    }

    [Fact]
    public void SaveToAPathHeldOpenElsewhere_failsBeforeLayout_soTheDocumentIsStillUsable()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "report.pdf");
        File.WriteAllText(target, "old contents");

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => doc.Save(target));

        Assert.Equal("old contents", File.ReadAllText(target));

        // The lock was found before anything was laid out, so nothing was added to the document.
        doc.Save(target);
        Assert.Equal(1, Pages(File.ReadAllBytes(target)));
    }

    [Fact]
    public async Task AsyncSaveToAPathHeldOpenElsewhere_failsBeforeLayout_soTheDocumentIsStillUsable()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "report.pdf");
        await File.WriteAllTextAsync(target, "old contents", TestContext.Current.CancellationToken);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => doc.SaveAsync(target, TestContext.Current.CancellationToken));

        await doc.SaveAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal(1, Pages(await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void SaveToASymbolicLink_writesThroughToItsTarget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        File.WriteAllText(real, "old contents");
        File.CreateSymbolicLink(link, real);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.Save(link);

        // The link is still a link and the file behind it holds the PDF. A temporary file moved
        // over the path would have replaced the link with a regular file.
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(1, Pages(File.ReadAllBytes(real)));
    }

    [Fact]
    public async Task AsyncSaveToASymbolicLink_writesThroughToItsTarget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        await File.WriteAllTextAsync(real, "old contents", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(link, real);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        await doc.SaveAsync(link, TestContext.Current.CancellationToken);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(1, Pages(await File.ReadAllBytesAsync(real, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void SaveToADeviceFile_doesNotTryToTruncateIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/dev/null does not exist on Windows.");

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        doc.Save("/dev/null");   // cannot be truncated: SetLength throws IOException
    }

    // ── #530: a step after the layout can fail too ───────────────────────────

    /// <summary>
    /// Cancels the given source from inside the layout pass, so the token is already cancelled when
    /// the layout returns and the next step that looks at it throws. No timing involved.
    /// </summary>
    private sealed class CancelDuringLayoutRenderer(CancellationTokenSource cts) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            cts.Cancel();
            return LayoutResult.Full(context.Area.WithHeight(0));
        }

        public void Draw(DrawContext context) { }
    }

    private static Document CancelsDuringLayout(CancellationTokenSource cts)
    {
        var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.Add(new CancelDuringLayoutRenderer(cts));
        return doc;
    }

    [Fact]
    public async Task AsyncSaveCancelledAfterLayout_refusesTheRetryInsteadOfAppendingASecondLayout()
    {
        using var cts = new CancellationTokenSource();
        using var doc = CancelsDuringLayout(cts);

        using var first = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.SaveAsync(first, cts.Token));
        Assert.Equal(0, first.Length);

        using var second = new MemoryStream();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SaveAsync(second, TestContext.Current.CancellationToken));

        Assert.Contains("cannot be saved again", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, second.Length);   // used to be a file with two layouts' pages
    }

    [Fact]
    public async Task AsyncSaveToAPathCancelledAfterLayout_leavesTheFile_andRefusesTheRetry()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "report.pdf");
        await File.WriteAllTextAsync(target, "old contents", TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        using var doc = CancelsDuringLayout(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.SaveAsync(target, cts.Token));
        Assert.Equal("old contents", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SaveAsync(target, TestContext.Current.CancellationToken));
        Assert.Contains("cannot be saved again", ex.Message, StringComparison.Ordinal);
        Assert.Equal("old contents", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AsyncSaveAfterASuccessfulSave_stillReportsAlreadyWritten()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        using var first = new MemoryStream();
        await doc.SaveAsync(first, TestContext.Current.CancellationToken);

        using var second = new MemoryStream();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SaveAsync(second, TestContext.Current.CancellationToken));

        Assert.Contains("already been written", ex.Message, StringComparison.Ordinal);
    }

    // ── Signing refuses encryption before it lays anything out ───────────────

    [Fact]
    public void SignWithEncryption_isRefusedBeforeLayout_soTheDocumentStillSavesOnePage()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=VellumPdf Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });

        using var signed = new MemoryStream();
        Assert.Throws<NotSupportedException>(() =>
            doc.Sign(signed, new PdfSignatureSettings { Certificate = cert, SignerName = "Tester", Reason = "Test" }));
        Assert.Equal(0, signed.Length);

        // The refusal came before the layout, so the pages were not added twice.
        Assert.Equal(1, Pages(Save(doc)));    // used to be 2
    }
}
