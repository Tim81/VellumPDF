// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Images;
using VellumPdf.IO;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Where <c>SaveAsync(string)</c> looks at its token, and what each point leaves behind. 2.3.3 opened the
/// path with <c>FileMode.Create</c>, so the file was empty before any of them; now the file is emptied
/// only once the PDF is built, and a cancellation up to that point leaves it as it was.
/// </summary>
public sealed class SaveAsyncCancellationTests
{
    private static readonly byte[] Original = Enumerable.Repeat((byte)'X', 1234).ToArray();

    /// <summary>
    /// A PDF object that cancels a token from inside the serialisation, a point no timing could hit
    /// reliably. It is the value of an entry in an image's SMask dictionary, so it is written while
    /// the PDF is being built, after the layout and before the file is emptied.
    /// </summary>
    private sealed class CancelWhenWritten(CancellationTokenSource cts) : PdfObject
    {
        public int Writes { get; private set; }

        public override void WriteTo(PdfWriter writer)
        {
            Writes++;
            cts.Cancel();
            new PdfInteger(1).WriteTo(writer);
        }
    }

    private static (Document Doc, CancelWhenWritten Hook) CancelsDuringSerialisation(CancellationTokenSource cts)
    {
        var image = PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbaPng());
        var hook = new CancelWhenWritten(cts);
        image.SMask!.Dictionary.Set(new PdfName("CancelHook"), hook);
        var doc = new Document();
        doc.Add(new Paragraph("x"));
        doc.Add(new LayoutImage(image));
        return (doc, hook);
    }

    private static string TempPdf() => Path.Combine(Path.GetTempPath(), "cancel-" + Guid.NewGuid().ToString("N") + ".pdf");

    /// <summary>
    /// A cancellation during the serialisation is seen once the PDF is built, before the file is
    /// emptied: the file that was there is intact, and the task ends with TaskCanceledException, the
    /// type 2.3.3 threw. The hook firing is asserted, so a save that never reached the serialisation
    /// cannot pass.
    /// </summary>
    [Fact]
    public async Task CancelDuringSerialisation_leavesTheExistingFileIntact_andThrowsTaskCanceled()
    {
        var path = TempPdf();
        await File.WriteAllBytesAsync(path, Original, TestContext.Current.CancellationToken);
        try
        {
            using var cts = new CancellationTokenSource();
            var (doc, hook) = CancelsDuringSerialisation(cts);
            using (doc)
            {
                await Assert.ThrowsAsync<TaskCanceledException>(() => doc.SaveAsync(path, cts.Token));
            }

            Assert.True(hook.Writes > 0);
            Assert.Equal(Original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The same save with a token that is not cancelled completes and replaces the file, so the
    /// fixture above does reach the point it claims to.
    /// </summary>
    [Fact]
    public async Task SerialisationHook_withAnUncancelledToken_saves()
    {
        var path = TempPdf();
        await File.WriteAllBytesAsync(path, Original, TestContext.Current.CancellationToken);
        try
        {
            // A source nobody awaits: the hook cancels it, the save is given another token.
            using var unrelated = new CancellationTokenSource();
            var (doc, hook) = CancelsDuringSerialisation(unrelated);
            using (doc)
            {
                await doc.SaveAsync(path, TestContext.Current.CancellationToken);
            }

            Assert.True(hook.Writes > 0);
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.StartsWith("%PDF-", System.Text.Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A cancellation seen in the copy, after the file was emptied, throws TaskCanceledException and
    /// leaves the file truncated: the one point where it can. The token is cancelled from the seam that
    /// runs between emptying the file and the copy, so the copy's own token is what is under test.
    /// </summary>
    [Theory]
    [InlineData(1 << 20)]
    [InlineData(7)]
    public async Task CancelInTheCopy_throwsTaskCanceled_andLeavesTheFileTruncated(int chunkSize)
    {
        var path = TempPdf();
        await File.WriteAllBytesAsync(path, Original, TestContext.Current.CancellationToken);
        try
        {
            using var cts = new CancellationTokenSource();
            using var doc = new Document { SaveBufferChunkSize = chunkSize, BeforeSaveCopy = _ => cts.Cancel() };
            doc.Add(new Paragraph("x"));

            await Assert.ThrowsAsync<TaskCanceledException>(() => doc.SaveAsync(path, cts.Token));

            Assert.Equal(0, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A token that is already cancelled wins over every other defect, as on 2.3.3, where the first
    /// thing a <c>SaveAsync</c> did was hand it to <c>Task.Run</c>: a null destination, a stream that
    /// cannot be written to and a document with no pages all end with TaskCanceledException.
    /// </summary>
    [Theory]
    [InlineData("null destination")]
    [InlineData("read-only stream")]
    [InlineData("no pages")]
    public async Task APreCancelledToken_winsOverEveryOtherDefect_asOn1c61fa6(string defect)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var doc = new Document();
        if (defect != "no pages") doc.Add(new Paragraph("x"));
        using var readOnly = new MemoryStream([], writable: false);
        var destination = defect == "read-only stream" ? readOnly : defect == "null destination" ? null! : new MemoryStream();

        await Assert.ThrowsAsync<TaskCanceledException>(() => doc.SaveAsync(destination, cts.Token));

        var path = TempPdf();
        await Assert.ThrowsAsync<TaskCanceledException>(() => doc.SaveAsync(path, cts.Token));
        Assert.False(File.Exists(path));
    }
}
