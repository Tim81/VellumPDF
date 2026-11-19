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
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>What a <see cref="Document"/> is left in by a save that threw, and what a failed save to a path does to the file that was already there.</summary>
public sealed class SaveLifecycleTests
{
    private static TextStyle Style(double size = 12) =>
        new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static Document Good() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 200),
        Margins = new EdgeInsets(10),
    };

    /// <summary>300pt text on a 300x200pt page cannot be placed.</summary>
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

    /// <summary>Creates a symbolic link, or skips the test when this user may not make one (Windows needs a privilege or developer mode).</summary>
    private static void CreateLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("This user cannot create a symbolic link here: " + ex.Message);
        }
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

        // The caller enlarges the page and tries again.
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

    // ── Refusals that need no layout come before it ──────────────────────────

    [Fact]
    public void NullDestination_isRefusedBeforeLayout_soTheRetryWritesOnePage()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        var ex = Assert.Throws<ArgumentNullException>(() => doc.Save((Stream)null!));
        Assert.Equal("destination", ex.ParamName);

        Assert.Equal(1, Pages(Save(doc)));
    }

    [Fact]
    public void UnwritableStream_isRefusedBeforeLayout_andDoesNotBurnTheDocument()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        using var readOnly = new MemoryStream(new byte[8], writable: false);

        var ex = Assert.Throws<ArgumentException>(() => doc.Save(readOnly));
        Assert.Equal("destination", ex.ParamName);

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
        Assert.Equal(1, Pages(Save(doc)));
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
    public async Task AsyncSave_refusesTheSameThingsBeforeLayout_andTheRetrySavesOnce()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        using var readOnly = new MemoryStream(new byte[8], writable: false);

        await Assert.ThrowsAsync<NotSupportedException>(() => doc.SaveAsync(readOnly, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => doc.SaveAsync((Stream)null!, TestContext.Current.CancellationToken));

        using var ok = new MemoryStream();
        await doc.SaveAsync(ok, TestContext.Current.CancellationToken);
        Assert.Equal(1, Pages(ok.ToArray()));
    }

    /// <summary>A call with two defects, a save precondition and a layout defect, reports the precondition, because it is checked before anything is laid out and so stays retryable.</summary>
    [Fact]
    public void ANullDestination_isReportedBeforeALayoutDefect_asRuled()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("x", new TextStyle { FontSize = double.NaN }));

        Assert.Throws<ArgumentNullException>(() => doc.Save((Stream)null!));
    }

    /// <summary>A save that fails before any layout leaves the document usable, and the retry saves it once.</summary>
    [Fact]
    public void ARetryAfterAPreconditionFailure_savesTheDocumentOnce()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        using var readOnly = new MemoryStream(new byte[8], writable: false);

        Assert.Throws<ArgumentNullException>(() => doc.Save((Stream)null!));
        Assert.Throws<ArgumentException>(() => doc.Save(readOnly));

        Assert.Equal(1, Pages(Save(doc)));
    }

    // ── A document that was written is written once ──────────────────────────

    [Fact]
    public void SavingTwice_stillThrowsTheAlreadyWrittenMessage()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        Save(doc);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("already been written", ex.Message, StringComparison.Ordinal);
    }

    // ── A failed save to a path leaves the file that was there ───────────────

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

        Assert.Equal(before, File.ReadAllBytes(target));
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

        // A directory is refused by the open, not by a check of ours: the exception is the operating system's
        // UnauthorizedAccessException and nothing was created or removed.
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

    /// <summary>A read-only file is refused if, and only if, the operating system refuses to open it for writing; .NET reads a Unix read-only attribute from mode bits, which ignore root, so the test asks the open.</summary>
    [Fact]
    public void SaveToAReadOnlyFile_isRefusedExactlyWhenTheOpenRefusesIt()
    {
        using var dir = new TempDir();
        var readOnly = Path.Combine(dir.Path, "ro.pdf");
        File.WriteAllText(readOnly, "keep");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        // On non-root CI the open is refused; only a run as root (a container, say) reaches the overwrite branch.
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

    /// <summary>A Hidden or System file at the path is refused by the save itself, for both overloads, and left as it was; Windows only, since elsewhere Hidden is a leading dot in the name.</summary>
    [Theory]
    [InlineData(FileAttributes.Hidden, false)]
    [InlineData(FileAttributes.System, false)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, false)]
    [InlineData(FileAttributes.Hidden, true)]
    [InlineData(FileAttributes.System, true)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, true)]
    public async Task SaveToAHiddenOrSystemFile_isRefused(FileAttributes attributes, bool async)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("The Hidden and System file attributes refuse FileMode.Create on Windows only.");

        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "hidden.pdf");
        await File.WriteAllTextAsync(target, "keep", TestContext.Current.CancellationToken);
        File.SetAttributes(target, attributes);
        try
        {
            using var doc = Good();
            doc.Add(new Paragraph("Hello", Style()));

            if (async)
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => doc.SaveAsync(target, TestContext.Current.CancellationToken));
            else
                Assert.Throws<UnauthorizedAccessException>(() => doc.Save(target));

            Assert.Equal("keep", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));

            // Refused before the layout, so the document is untouched and saves elsewhere.
            var ok = Path.Combine(dir.Path, "ok.pdf");
            doc.Save(ok);
            Assert.Equal(1, Pages(File.ReadAllBytes(ok)));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    /// <summary>Through a symbolic link the final target's Hidden and System attributes decide, not the link's, for both overloads; the value is 2.3.3's.</summary>
    [Theory]
    [InlineData(FileAttributes.Normal, FileAttributes.Hidden, true, false)]
    [InlineData(FileAttributes.Normal, FileAttributes.System, true, true)]
    [InlineData(FileAttributes.Hidden, FileAttributes.Normal, false, false)]
    [InlineData(FileAttributes.Hidden, FileAttributes.Normal, false, true)]
    [InlineData(FileAttributes.Hidden, FileAttributes.Hidden, true, true)]
    public async Task SaveThroughALink_judgesTheFinalTargetsAttributes(
        FileAttributes linkAttributes, FileAttributes targetAttributes, bool refused, bool async)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("The Hidden and System file attributes refuse FileMode.Create on Windows only.");

        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        await File.WriteAllTextAsync(real, "keep", TestContext.Current.CancellationToken);
        CreateLink(link, real);
        if (linkAttributes != FileAttributes.Normal)
            new FileInfo(link).Attributes = linkAttributes | FileAttributes.ReparsePoint;
        if (targetAttributes != FileAttributes.Normal)
            File.SetAttributes(real, targetAttributes);
        try
        {
            using var doc = Good();
            doc.Add(new Paragraph("Hello", Style()));

            if (refused)
            {
                if (async)
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => doc.SaveAsync(link, TestContext.Current.CancellationToken));
                else
                    Assert.Throws<UnauthorizedAccessException>(() => doc.Save(link));
                File.SetAttributes(real, FileAttributes.Normal);
                Assert.Equal("keep", await File.ReadAllTextAsync(real, TestContext.Current.CancellationToken));
            }
            else
            {
                if (async)
                    await doc.SaveAsync(link, TestContext.Current.CancellationToken);
                else
                    doc.Save(link);
                File.SetAttributes(real, FileAttributes.Normal);
                Assert.Equal(1, Pages(File.ReadAllBytes(real)));
            }
        }
        finally
        {
            File.SetAttributes(real, FileAttributes.Normal);
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
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        File.WriteAllText(real, "old contents");
        CreateLink(link, real);

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
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        await File.WriteAllTextAsync(real, "old contents", TestContext.Current.CancellationToken);
        CreateLink(link, real);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        await doc.SaveAsync(link, TestContext.Current.CancellationToken);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(1, Pages(await File.ReadAllBytesAsync(real, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void SaveToADeviceFile_doesNotTryToTruncateIt()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));

        // The null device: /dev/null, or NUL on Windows. Neither can be truncated.
        doc.Save(OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
    }

    // ── A step after the layout can fail too ─────────────────────────────────

    /// <summary>Cancels the given source from inside the layout pass, so the next step that looks at the token throws; no timing involved.</summary>
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
        Assert.Equal(0, second.Length);
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
        Assert.Equal(1, Pages(Save(doc)));
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

    /// <summary>A symbolic link counts as something at the path, a dangling one included, so a failed save never deletes it; the open creates the link's destination, so an empty file there is the accepted gap.</summary>
    [Fact]
    public void FailedSaveThroughADanglingLink_keepsTheLink_andLeavesTheEmptyFileTheOpenCreated()
    {
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        CreateLink(link, real);        // the target does not exist

        using var bad = TooTall();
        Assert.Throws<InvalidOperationException>(() => bad.Save(link));

        Assert.NotNull(new FileInfo(link).LinkTarget);      // the link survived: it was not deleted
        Assert.True(File.Exists(real));                     // the open created the destination
        Assert.Equal(0, new FileInfo(real).Length);         // and nothing was written to it
    }

    [Fact]
    public async Task FailedAsyncSaveThroughADanglingLink_keepsTheLink_andLeavesTheEmptyFileTheOpenCreated()
    {
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        CreateLink(link, real);

        using var bad = TooTall();
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.SaveAsync(link, TestContext.Current.CancellationToken));

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.True(File.Exists(real));
        Assert.Equal(0, new FileInfo(real).Length);
    }

    [Fact]
    public void SaveThroughADanglingLink_createsTheTarget_andKeepsTheLink()
    {
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        CreateLink(link, real);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        doc.Save(link);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(1, Pages(File.ReadAllBytes(real)));
    }

    [Fact]
    public async Task AsyncSaveThroughADanglingLink_createsTheTarget_andKeepsTheLink()
    {
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var link = Path.Combine(dir.Path, "link.pdf");
        CreateLink(link, real);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        await doc.SaveAsync(link, TestContext.Current.CancellationToken);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(1, Pages(await File.ReadAllBytesAsync(real, TestContext.Current.CancellationToken)));
    }

    /// <summary>A relative link target is resolved by the operating system against the directory the link lives in, so <c>../out.pdf</c> below a link to a directory is that directory's parent.</summary>
    [Fact]
    public void SaveThroughARelativeLinkWithDotDotBelowASymlinkedDirectory_writesTheFileTheOperatingSystemResolves()
    {
        using var dir = new TempDir();
        var deep = Path.Combine(dir.Path, "a", "b");
        Directory.CreateDirectory(deep);
        var linkedDir = Path.Combine(dir.Path, "linkdir");
        CreateLink(linkedDir, deep);                         // absolute link to a directory
        var link = Path.Combine(linkedDir, "link.pdf");                   // really a/b/link.pdf
        CreateLink(link, Path.Combine("..", "out.pdf"));     // relative: a/out.pdf

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
        using var dir = new TempDir();
        var deep = Path.Combine(dir.Path, "a", "b");
        Directory.CreateDirectory(deep);
        var linkedDir = Path.Combine(dir.Path, "linkdir");
        CreateLink(linkedDir, deep);
        var link = Path.Combine(linkedDir, "link.pdf");
        CreateLink(link, Path.Combine("..", "out.pdf"));

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        await doc.SaveAsync(link, TestContext.Current.CancellationToken);

        Assert.Equal(1, Pages(await File.ReadAllBytesAsync(Path.Combine(dir.Path, "a", "out.pdf"), TestContext.Current.CancellationToken)));
        Assert.False(File.Exists(Path.Combine(dir.Path, "out.pdf")));
    }

    private static System.Diagnostics.Process? StartCat(string fifo)
    {
        try
        {
            return System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("cat", fifo) { RedirectStandardOutput = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>A path that is a pipe is written into; the save runs on its own task with a timeout, because opening a pipe for writing blocks until a reader is there.</summary>
    [Fact]
    public async Task SaveToANamedPipe_writesTheWholePdfIntoIt()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "A named pipe is made with mkfifo, which this test uses on Linux only.");

        using var dir = new TempDir();
        var fifo = Path.Combine(dir.Path, "pipe");
        try
        {
            using var mkfifo = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("mkfifo", fifo) { RedirectStandardError = true });
            await mkfifo!.WaitForExitAsync(TestContext.Current.CancellationToken);
            if (mkfifo.ExitCode != 0)
                OracleGate.Unavailable("mkfifo", "mkfifo exited with code " + mkfifo.ExitCode + ".");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            OracleGate.Unavailable("mkfifo", "the program could not be started.");
        }

        // Opening a pipe blocks until the other end is open, so the reader is a separate process: cat, because .NET
        // takes an advisory lock on every file it opens and a reader in this process would conflict.
        using var cat = StartCat(fifo);
        if (cat is null)
            OracleGate.Unavailable("cat", "the program could not be started; it reads the named pipe.");
        var received = new MemoryStream();
        var reader = cat!.StandardOutput.BaseStream.CopyToAsync(received, TestContext.Current.CancellationToken);
        System.Diagnostics.Process? rescue = null;

        try
        {
            using var doc = Good();
            doc.Add(new Paragraph("Hello", Style()));
            var save = Task.Run(() => doc.Save(fifo), TestContext.Current.CancellationToken);

            try
            {
                await save.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                // The writer is stuck opening the pipe: give it a reader so the task ends, then fail.
                rescue = StartCat(fifo);
                if (rescue is not null) _ = rescue.StandardOutput.BaseStream.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
                await Task.WhenAny(save, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                Assert.Fail("Save to a named pipe did not finish within 30 seconds.");
            }

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
            if (rescue is { HasExited: false }) rescue.Kill();
            rescue?.Dispose();
        }
    }

    /// <summary>On Linux, <c>/dev/stdout</c> resolves through <c>/proc/self/fd/1</c> to a target that is not a file name, so the operating system must open the link itself; the test builds the same chain over an anonymous pipe.</summary>
    [Fact]
    public async Task SaveThroughALinkToAProcFdPipe_writesTheWholePdfIntoThePipe()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "/proc/self/fd is Linux-specific.");
        Assert.SkipWhen(!Directory.Exists("/proc/self/fd"), "/proc is not mounted.");

        using var dir = new TempDir();
        using var server = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.Out, HandleInheritability.None);
        using var client = new System.IO.Pipes.AnonymousPipeClientStream(
            System.IO.Pipes.PipeDirection.In, server.ClientSafePipeHandle);

        var fd = (int)server.SafePipeHandle.DangerousGetHandle();
        var link = Path.Combine(dir.Path, "stdout");
        File.CreateSymbolicLink(link, $"/proc/self/fd/{fd}");

        var received = new MemoryStream();
        var reader = client.CopyToAsync(received, TestContext.Current.CancellationToken);

        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        var save = Task.Run(() => doc.Save(link), TestContext.Current.CancellationToken);
        await save.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The save closed its own descriptor; closing the server's end is what gives the reader EOF.
        server.Dispose();
        await reader.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var bytes = received.ToArray();
        Assert.StartsWith("%PDF-", System.Text.Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(1, Pages(bytes));
        Assert.NotNull(new FileInfo(link).LinkTarget);      // the link is still the link
    }

    [Fact]
    public void FailedSaveThroughAChainOfLinks_leavesTheExistingFinalFileAndBothLinks()
    {
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real.pdf");
        var middle = Path.Combine(dir.Path, "middle.pdf");
        var outer = Path.Combine(dir.Path, "outer.pdf");
        File.WriteAllText(real, "old contents");
        CreateLink(middle, real);
        CreateLink(outer, middle);

        using var bad = TooTall();
        Assert.Throws<InvalidOperationException>(() => bad.Save(outer));

        Assert.Equal("old contents", File.ReadAllText(real));
        Assert.NotNull(new FileInfo(outer).LinkTarget);
        Assert.NotNull(new FileInfo(middle).LinkTarget);
    }

    // ── A layout that started cannot be repeated, even before it added a page ──

    // A renderer caches its wrap at the width it was first laid out at, so a retry after a PageSize change
    // would reuse it. The document refuses the retry; recover with a new Document.
    [Fact]
    public void TooTallElementWithAHeader_isRefusedByTheCountingPass_andTheRetryIsRefusedToo()
    {
        // 150pt text needs a 180pt line; the 300x200 page with a 20pt header leaves 160pt.
        Document Build(PdfRectangle page)
        {
            var d = Good();
            d.PageSize = page;
            d.Add(new Paragraph("Hello world", Style(150)));
            d.Header = new RunningBand("h", Style()) { Height = 20 };
            return d;
        }

        using var doc = Build(new PdfRectangle(0, 0, 300, 200));
        Assert.Throws<InvalidOperationException>(() => Save(doc));

        doc.PageSize = new PdfRectangle(0, 0, 600, 800);
        var retry = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("new Document", retry.Message);

        using var fresh = Build(new PdfRectangle(0, 0, 600, 800));
        Assert.Equal(1, Pages(Save(fresh)));
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
    [InlineData(0, 536_870_895)] // one more than a placeholder can hold
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

        Assert.Equal(1, Pages(Save(doc)));
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

    // ── An empty document is refused when it is signed ───────────────────────

    [Theory]
    [InlineData(-1, 8192)]       // a bad signature page
    [InlineData(0, -1)]          // a bad placeholder size
    [InlineData(0, 8192)]        // good options
    public void SignAnEmptyDocument_reportsNoPages_whateverTheOptions_andStaysUsable(int signaturePage, int estimatedSize)
    {
        using var cert = SelfSigned();
        using var doc = Good();      // nothing added

        using var signed = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            doc.Sign(signed, new PdfSignatureSettings
            {
                Certificate = cert,
                SignaturePage = signaturePage,
                EstimatedSignatureSizeBytes = estimatedSize,
            }));
        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, signed.Length);

        // Nothing was laid out and nothing marked as failed: with content added, it saves one page.
        doc.Add(new Paragraph("Hello", Style()));
        Assert.Equal(1, Pages(Save(doc)));
    }

    // ── The lifecycle checks of the path overloads, one by one ───────────────

    private static byte[] OldReport(string path)
    {
        using var good = Good();
        good.Add(new Paragraph("Old report", Style()));
        good.Save(path);
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void SaveToAPath_afterALayoutThatThrew_isRefused_andCreatesNothing()
    {
        using var dir = new TempDir();
        var second = Path.Combine(dir.Path, "second.pdf");
        using var doc = TooTall();
        Assert.Throws<InvalidOperationException>(() => Save(doc));
        doc.PageSize = new PdfRectangle(0, 0, 600, 800);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(second));

        Assert.Contains("cannot be saved again", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task SaveAsyncToAPath_afterALayoutThatThrew_isRefused_andCreatesNothing()
    {
        using var dir = new TempDir();
        var second = Path.Combine(dir.Path, "second.pdf");
        using var doc = TooTall();
        Assert.Throws<InvalidOperationException>(() => Save(doc));
        doc.PageSize = new PdfRectangle(0, 0, 600, 800);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SaveAsync(second, TestContext.Current.CancellationToken));

        Assert.Contains("cannot be saved again", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void SaveToAnExistingPath_afterALayoutThatThrew_leavesTheFileAlone()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "report.pdf");
        var before = OldReport(path);
        using var doc = TooTall();
        Assert.Throws<InvalidOperationException>(() => Save(doc));
        doc.PageSize = new PdfRectangle(0, 0, 600, 800);

        Assert.Throws<InvalidOperationException>(() => doc.Save(path));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Sign_afterALayoutThatThrew_isRefusedInsteadOfLayingOutAgain()
    {
        using var cert = SelfSigned();
        using var doc = TooTall();
        Assert.Throws<InvalidOperationException>(() => Save(doc));
        doc.PageSize = new PdfRectangle(0, 0, 600, 800);

        using var signed = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            doc.Sign(signed, new PdfSignatureSettings { Certificate = cert, SignerName = "Tester", Reason = "Test" }));

        Assert.Contains("cannot be saved again", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, signed.Length);
    }

    // ── The diagnostics are collected by the path overloads too ──────────────

    [Fact]
    public void SaveToAPath_populatesTheTextEncodingWarnings()
    {
        using var dir = new TempDir();
        using var doc = Good();
        doc.Add(new Paragraph("Black star: \u2605", Style()));

        doc.Save(Path.Combine(dir.Path, "star.pdf"));

        var warning = Assert.Single(doc.TextEncodingWarnings);
        Assert.Equal(new System.Text.Rune(0x2605), warning.Character);
    }

    [Fact]
    public async Task SaveAsyncToAPath_populatesTheTextEncodingWarnings()
    {
        using var dir = new TempDir();
        using var doc = Good();
        doc.Add(new Paragraph("Black star: \u2605", Style()));

        await doc.SaveAsync(Path.Combine(dir.Path, "star.pdf"), TestContext.Current.CancellationToken);

        var warning = Assert.Single(doc.TextEncodingWarnings);
        Assert.Equal(new System.Text.Rune(0x2605), warning.Character);
    }

    // ── Refusals that need no layout leave a file at the path alone ──────────

    [Fact]
    public void SaveToAPath_ofADisposedDocument_leavesTheExistingFileByteForByte()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "report.pdf");
        var before = OldReport(path);
        var doc = Good();
        doc.Add(new Paragraph("New", Style()));
        doc.Dispose();

        Assert.Throws<ObjectDisposedException>(() => doc.Save(path));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void SaveToAPath_ofADocumentAlreadyWritten_leavesTheFileByteForByte()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "report.pdf");
        using var doc = Good();
        doc.Add(new Paragraph("Once", Style()));
        doc.Save(path);
        var before = File.ReadAllBytes(path);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(path));

        Assert.Contains("already been written", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task SaveAsyncToAPath_ofADocumentAlreadyWritten_leavesTheFileByteForByte()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "report.pdf");
        using var doc = Good();
        doc.Add(new Paragraph("Once", Style()));
        await doc.SaveAsync(path, TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SaveAsync(path, TestContext.Current.CancellationToken));

        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SaveToAPath_withOptionsThatCannotBeHonoured_leavesTheFileByteForByte_andANewPathEmpty()
    {
        using var dir = new TempDir();
        var existing = Path.Combine(dir.Path, "report.pdf");
        var fresh = Path.Combine(dir.Path, "fresh.pdf");
        var before = OldReport(existing);
        using var doc = Good();
        doc.Add(new Paragraph("New", Style()));
        doc.UseObjectStreams = true;
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });

        Assert.Throws<NotSupportedException>(() => doc.Save(existing));
        Assert.Throws<NotSupportedException>(() => doc.Save(fresh));

        Assert.Equal(before, File.ReadAllBytes(existing));
        Assert.False(File.Exists(fresh));

        doc.UseObjectStreams = false;
        doc.Conformance = PdfConformance.PdfA2b;
        Assert.Throws<InvalidOperationException>(() => doc.Save(existing));
        Assert.Equal(before, File.ReadAllBytes(existing));
    }

    [Fact]
    public async Task SaveAsyncToAPath_withOptionsThatCannotBeHonoured_leavesTheFileByteForByte()
    {
        using var dir = new TempDir();
        var existing = Path.Combine(dir.Path, "report.pdf");
        var before = OldReport(existing);
        using var doc = Good();
        doc.Add(new Paragraph("New", Style()));
        doc.UseObjectStreams = true;
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });

        await Assert.ThrowsAsync<NotSupportedException>(() => doc.SaveAsync(existing, TestContext.Current.CancellationToken));

        Assert.Equal(before, await File.ReadAllBytesAsync(existing, TestContext.Current.CancellationToken));
    }

    // ── An empty Document saved to a path ────────────────────────────────────

    [Fact]
    public void SaveAnEmptyDocumentToANewPath_reportsNoPages_andLeavesNoFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "empty.pdf");
        using var doc = Good();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(path));

        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task SaveAsyncAnEmptyDocumentToANewPath_reportsNoPages_andLeavesNoFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "empty.pdf");
        using var doc = Good();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SaveAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SaveAnEmptyDocumentOverAnExistingFile_leavesTheFileByteForByte()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "report.pdf");
        var before = OldReport(path);
        using var doc = Good();

        Assert.Throws<InvalidOperationException>(() => doc.Save(path));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ── A failure while the PDF is built, after the layout, keeps the old file ──

    /// <summary>An annotation rectangle that is not a number: the layout accepts it and writing the PDF refuses it.</summary>
    private sealed class BadLinkRectangleRenderer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context) =>
            context.AddUriLinkAnnotation(new LayoutBox(double.NaN, 0, 10, 10), "https://example.com");
    }

    [Fact]
    public void SyncSaveToAPath_thatFailsWhileTheFileIsBuilt_keepsTheOldFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "report.pdf");
        var before = OldReport(path);
        using var doc = Good();
        doc.Add(new Paragraph("New", Style()));
        doc.Add(new BadLinkRectangleRenderer());

        Assert.Throws<ArgumentException>(() => doc.Save(path));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void SyncSaveToANewPath_thatFailsWhileTheFileIsBuilt_leavesNoFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "fresh.pdf");
        using var doc = Good();
        doc.Add(new Paragraph("New", Style()));
        doc.Add(new BadLinkRectangleRenderer());

        Assert.Throws<ArgumentException>(() => doc.Save(path));

        Assert.False(File.Exists(path));
    }
}
