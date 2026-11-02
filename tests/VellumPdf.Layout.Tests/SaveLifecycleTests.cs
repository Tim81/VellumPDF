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
    public void SaveToAPath_stillRefusesWhatTheOpenAlwaysRefused()
    {
        using var dir = new TempDir();
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        Assert.Throws<ArgumentNullException>(() => doc.Save((string)null!));
        Assert.Throws<ArgumentException>(() => doc.Save(string.Empty));
        Assert.Throws<DirectoryNotFoundException>(() => doc.Save(Path.Combine(dir.Path, "missing", "x.pdf")));

        // A directory is refused by the open, as on main, not by a check of ours: the exception is
        // the operating system's UnauthorizedAccessException and nothing was created or removed.
        var ex = Assert.Throws<UnauthorizedAccessException>(() => doc.Save(dir.Path));
        Assert.Contains(dir.Path, ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(dir.Path));

        // None of those refusals touched the document, so it still saves.
        var ok = Path.Combine(dir.Path, "ok.pdf");
        doc.Save(ok);
        Assert.Equal(1, Pages(File.ReadAllBytes(ok)));
    }

    [Fact]
    public async Task SaveAsyncToADirectory_isRefusedByTheOpen_andTheDocumentIsStillUsable()
    {
        using var dir = new TempDir();
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => doc.SaveAsync(dir.Path, TestContext.Current.CancellationToken));

        var ok = Path.Combine(dir.Path, "ok.pdf");
        await doc.SaveAsync(ok, TestContext.Current.CancellationToken);
        Assert.Equal(1, Pages(await File.ReadAllBytesAsync(ok, TestContext.Current.CancellationToken)));
    }

    /// <summary>
    /// A read-only file is refused if, and only if, the operating system refuses to open it for
    /// writing. .NET reads a Unix file's read-only attribute from its mode bits, which ignore root, so
    /// the test asks the open instead of assuming: as root the save overwrites the file, as main did,
    /// and there is nothing to assert about a refusal.
    /// </summary>
    [Fact]
    public void SaveToAReadOnlyFile_isRefusedExactlyWhenTheOpenRefusesIt()
    {
        using var dir = new TempDir();
        var readOnly = Path.Combine(dir.Path, "ro.pdf");
        File.WriteAllText(readOnly, "keep");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        // On non-root CI the open is refused, so this takes the first branch below. Only a run as
        // root (a container, say) reaches the overwrite branch.
        bool openRefused;
        try
        {
            using var probe = new FileStream(readOnly, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            openRefused = false;
        }
        catch (UnauthorizedAccessException)
        {
            openRefused = true;
        }

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        if (openRefused)
        {
            Assert.Throws<UnauthorizedAccessException>(() => doc.Save(readOnly));
            Assert.Equal("keep", File.ReadAllText(readOnly));

            // Refused by the open, before the layout, so the document is untouched.
            var ok = Path.Combine(dir.Path, "ok.pdf");
            doc.Save(ok);
            Assert.Equal(1, Pages(File.ReadAllBytes(ok)));
        }
        else
        {
            doc.Save(readOnly);
            Assert.Equal(1, Pages(File.ReadAllBytes(readOnly)));
        }
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
        await Assert.ThrowsAsync<TaskCanceledException>(() => doc.SaveAsync(first, cts.Token));
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

        await Assert.ThrowsAsync<TaskCanceledException>(() => doc.SaveAsync(target, cts.Token));
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

    // ── Cancellation is a TaskCanceledException at every checkpoint ───────────

    [Fact]
    public async Task AsyncSaveToAStreamWithATokenCancelledBeforeLayout_throwsTaskCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        using var ms = new MemoryStream();

        await Assert.ThrowsAsync<TaskCanceledException>(() => doc.SaveAsync(ms, cts.Token));

        // Nothing was laid out, so nothing is lost: the document saves with a live token.
        await doc.SaveAsync(ms, TestContext.Current.CancellationToken);
        Assert.Equal(1, Pages(ms.ToArray()));
    }

    [Fact]
    public async Task AsyncSaveToAPathWithATokenCancelledBeforeLayout_throwsTaskCanceledException_andCreatesNothing()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "never.pdf");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        await Assert.ThrowsAsync<TaskCanceledException>(() => doc.SaveAsync(target, cts.Token));

        Assert.False(File.Exists(target));
        await doc.SaveAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal(1, Pages(await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken)));
    }

    // ── A symbolic link is followed before anything is created or deleted ─────

    /// <summary>
    /// A symbolic link counts as something at the path, a dangling one included, so a failed save
    /// never deletes the link. The open follows the link and creates its destination, and the save
    /// cannot tell that this call did, so the accepted gap is an empty file there. The test records
    /// that state rather than hiding it.
    /// </summary>
    [Fact]
    public void FailedSaveThroughADanglingLink_keepsTheLink_andLeavesTheEmptyFileTheOpenCreated()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        File.CreateSymbolicLink(link, real);        // the target does not exist

        using var bad = TooTall();
        Assert.Throws<InvalidOperationException>(() => bad.Save(link));

        Assert.NotNull(new FileInfo(link).LinkTarget);      // the link survived: it was not deleted
        Assert.True(File.Exists(real));                     // the open created the destination
        Assert.Equal(0, new FileInfo(real).Length);         // and nothing was written to it
    }

    [Fact]
    public async Task FailedAsyncSaveThroughADanglingLink_keepsTheLink_andLeavesTheEmptyFileTheOpenCreated()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        File.CreateSymbolicLink(link, real);

        using var bad = TooTall();
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.SaveAsync(link, TestContext.Current.CancellationToken));

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.True(File.Exists(real));
        Assert.Equal(0, new FileInfo(real).Length);
    }

    [Fact]
    public void SaveThroughADanglingLink_createsTheTarget_andKeepsTheLink()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        File.CreateSymbolicLink(link, real);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.Save(link);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(1, Pages(File.ReadAllBytes(real)));
    }

    [Fact]
    public async Task AsyncSaveThroughADanglingLink_createsTheTarget_andKeepsTheLink()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        File.CreateSymbolicLink(link, real);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        await doc.SaveAsync(link, TestContext.Current.CancellationToken);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(1, Pages(await File.ReadAllBytesAsync(real, TestContext.Current.CancellationToken)));
    }

    /// <summary>
    /// A relative link target is resolved by the operating system against the directory the link
    /// really lives in, so <c>../out.pdf</c> below a symbolic link to a directory is that directory's
    /// parent, not the parent of the path the caller wrote. Collapsing the <c>..</c> by string rules
    /// writes a different file.
    /// </summary>
    [Fact]
    public void SaveThroughARelativeLinkWithDotDotBelowASymlinkedDirectory_writesTheFileTheOperatingSystemResolves()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        using var dir = new TempDir();
        var deep = Path.Combine(dir.Path, "a", "b");
        Directory.CreateDirectory(deep);
        var linkedDir = Path.Combine(dir.Path, "linkdir");
        File.CreateSymbolicLink(linkedDir, deep);                         // absolute link to a directory
        var link = Path.Combine(linkedDir, "link.pdf");                   // really a/b/link.pdf
        File.CreateSymbolicLink(link, Path.Combine("..", "out.pdf"));     // relative: a/out.pdf

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.Save(link);

        Assert.Equal(1, Pages(File.ReadAllBytes(Path.Combine(dir.Path, "a", "out.pdf"))));
        Assert.False(File.Exists(Path.Combine(dir.Path, "out.pdf")));     // where the string rules point
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task AsyncSaveThroughARelativeLinkWithDotDotBelowASymlinkedDirectory_writesTheFileTheOperatingSystemResolves()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        using var dir = new TempDir();
        var deep = Path.Combine(dir.Path, "a", "b");
        Directory.CreateDirectory(deep);
        var linkedDir = Path.Combine(dir.Path, "linkdir");
        File.CreateSymbolicLink(linkedDir, deep);
        var link = Path.Combine(linkedDir, "link.pdf");
        File.CreateSymbolicLink(link, Path.Combine("..", "out.pdf"));

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        await doc.SaveAsync(link, TestContext.Current.CancellationToken);

        Assert.Equal(1, Pages(await File.ReadAllBytesAsync(Path.Combine(dir.Path, "a", "out.pdf"), TestContext.Current.CancellationToken)));
        Assert.False(File.Exists(Path.Combine(dir.Path, "out.pdf")));
    }

    /// <summary>
    /// A path that is a pipe is written into, as on main: a link to <c>/dev/stdout</c> ends at
    /// <c>pipe:[N]</c>, which is not a file name. A named pipe stands in for it, with a reader that
    /// drains it and a guard that unblocks the reader if the save never opens the pipe.
    /// </summary>
    [Fact]
    public async Task SaveToANamedPipe_writesTheWholePdfIntoIt()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "mkfifo is needed.");

        using var dir = new TempDir();
        var fifo = Path.Combine(dir.Path, "pipe");
        try
        {
            using var mkfifo = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("mkfifo", fifo) { RedirectStandardError = true });
            await mkfifo!.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.SkipWhen(mkfifo.ExitCode != 0, "mkfifo failed.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("mkfifo is not available.");
        }

        // Opening a pipe blocks until the other end is open, so the reader is a separate process.
        // It is cat rather than a FileStream because .NET takes an advisory lock on every file it
        // opens, and a reader in this process would conflict with the writer's.
        using var cat = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cat", fifo) { RedirectStandardOutput = true });
        Assert.NotNull(cat);
        var received = new MemoryStream();
        var reader = cat.StandardOutput.BaseStream.CopyToAsync(received, TestContext.Current.CancellationToken);

        try
        {
            using var doc = Good();
            doc.Add(new Paragraph("Hello", Style()));
            doc.Save(fifo);

            await reader.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await cat.WaitForExitAsync(TestContext.Current.CancellationToken);

            var bytes = received.ToArray();
            Assert.StartsWith("%PDF-", System.Text.Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
            Assert.Equal(1, Pages(bytes));
        }
        finally
        {
            // If the save failed before opening the pipe, cat is still waiting for a writer.
            if (!cat.HasExited) cat.Kill();
        }
    }

    [Fact]
    public void FailedSaveThroughAChainOfLinks_leavesTheExistingFinalFileAndBothLinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var middle = Path.Combine(dir.Path, "middle.pdf");
        var outer = Path.Combine(dir.Path, "outer.pdf");
        File.WriteAllText(real, "old contents");
        File.CreateSymbolicLink(middle, real);
        File.CreateSymbolicLink(outer, middle);

        using var bad = TooTall();
        Assert.Throws<InvalidOperationException>(() => bad.Save(outer));

        Assert.Equal("old contents", File.ReadAllText(real));
        Assert.NotNull(new FileInfo(outer).LinkTarget);
        Assert.NotNull(new FileInfo(middle).LinkTarget);
    }

    // ── With a header or footer, a too-tall element is refused before any page is added ──

    [Fact]
    public void TooTallElementWithAHeader_isRefusedByTheCountingPass_soTheRetryIsAllowed()
    {
        // 150pt text needs a 180pt line; the 300x200 page with a 20pt header leaves 160pt.
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style(150)));
        doc.Header = new RunningBand("h", Style()) { Height = 20 };
        Assert.Throws<InvalidOperationException>(() => Save(doc));

        doc.PageSize = new PdfRectangle(0, 0, 600, 800);
        var recovered = Save(doc);

        // One page, as a fresh document makes it: nothing of the failed attempt was left behind.
        Assert.Equal(1, Pages(recovered));
    }

    // ── Signing refuses what no page count can make valid before the layout ──

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=VellumPdf Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Theory]
    [InlineData(-1, 8192)]       // a negative signature page
    [InlineData(0, -1)]          // a negative placeholder size
    [InlineData(0, 536_870_895)] // one more than a placeholder can hold: used to be OutOfMemory after layout
    public void SignWithAnOptionNoPageCountCouldMakeValid_isRefusedBeforeLayout_soTheDocumentStillSaves(
        int signaturePage, int estimatedSize)
    {
        using var cert = SelfSigned();
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        using var signed = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.Sign(signed, new PdfSignatureSettings
            {
                Certificate = cert,
                SignaturePage = signaturePage,
                EstimatedSignatureSizeBytes = estimatedSize,
            }));
        Assert.Equal(0, signed.Length);

        Assert.Equal(1, Pages(Save(doc)));    // used to be 2: the layout had run before the refusal
    }

    [Fact]
    public void SignWithAPageIndexPastTheLastPage_isStillRefusedAfterLayout()
    {
        using var cert = SelfSigned();
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        using var signed = new MemoryStream();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.Sign(signed, new PdfSignatureSettings { Certificate = cert, SignaturePage = 5 }));
        Assert.Contains("1 page(s)", ex.Message, StringComparison.Ordinal);
    }
}
