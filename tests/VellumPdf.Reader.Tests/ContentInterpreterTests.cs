// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using CsCheck;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Reader.Content;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Exercises the internal content-stream interpreter (#98, part 3): <see cref="ContentInterpreter"/>
/// walking a page's <c>/Contents</c> per ISO 32000-2 §7.8.2. Fixtures are hand-built byte strings
/// (the <c>PageTreeTests</c> style) so tests control exact operator sequences, resource shapes, and
/// deliberately malformed constructs a document writer cannot produce.
/// </summary>
public sealed class ContentInterpreterTests
{
    // ── Fixture builders ─────────────────────────────────────────────────────────────────────────

    private sealed record Obj(int Num, string Dict, byte[]? Stream = null);

    private static byte[] BuildPdf(int rootObjectNumber, params Obj[] objects)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");

        var maxNum = objects.Max(o => o.Num);
        var offsets = new int?[maxNum + 1];
        foreach (var obj in objects.OrderBy(o => o.Num))
        {
            offsets[obj.Num] = (int)ms.Position;
            if (obj.Stream is null)
            {
                W($"{obj.Num} 0 obj\n{obj.Dict}\nendobj\n");
            }
            else
            {
                var trimmed = obj.Dict.TrimEnd();
                var withLength = trimmed[..^2].TrimEnd() + $" /Length {obj.Stream.Length} >>";
                W($"{obj.Num} 0 obj\n{withLength}\nstream\n");
                ms.Write(obj.Stream);
                W("\nendstream\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {maxNum + 1}\n");
        W("0000000000 65535 f \n");
        for (var i = 1; i <= maxNum; i++)
        {
            W(offsets[i] is { } offset
                ? $"{offset:D10} 00000 n \n"
                : "0000000000 65535 f \n");
        }
        W($"trailer\n<< /Size {maxNum + 1} /Root {rootObjectNumber} 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    private static byte[] Flate(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    /// <summary>Builds a one-page document whose page's own content is <paramref name="content"/>,
    /// with <paramref name="resourcesDict"/> as its (already-formatted) <c>/Resources</c> value and
    /// any <paramref name="extraObjects"/> (fonts, XObjects, ExtGStates, ...) alongside it.</summary>
    private static byte[] BuildPageDoc(
        string content, string resourcesDict = "<< >>", params Obj[] extraObjects)
    {
        var objs = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + $"/Resources {resourcesDict} /Contents 4 0 R >>"),
            new(4, "<< >>", Encoding.ASCII.GetBytes(content)),
        };
        objs.AddRange(extraObjects);
        return BuildPdf(1, [.. objs]);
    }

    /// <summary>Same as <see cref="BuildPageDoc"/>, but takes the page's content as raw bytes.
    /// Needed whenever a fixture embeds bytes outside the ASCII range, which
    /// <c>Encoding.ASCII.GetBytes</c> would otherwise silently replace with '?'.</summary>
    private static byte[] BuildPageDocRaw(
        byte[] content, string resourcesDict = "<< >>", params Obj[] extraObjects)
    {
        var objs = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + $"/Resources {resourcesDict} /Contents 4 0 R >>"),
            new(4, "<< >>", content),
        };
        objs.AddRange(extraObjects);
        return BuildPdf(1, [.. objs]);
    }

    private sealed class RecordingVisitor : IContentVisitor
    {
        public List<(string Op, List<PdfObject> Operands, int Offset)> Operators { get; } = [];
        public List<(PdfDictionary Dict, byte[] Data, int Offset)> InlineImages { get; } = [];
        public List<(PdfDictionary Dict, Matrix Matrix, PdfRectangle? BBox, int ObjectNumber, int Offset)> FormBegins { get; } = [];
        public List<int> FormEnds { get; } = [];

        public void OnOperator(string operatorName, IReadOnlyList<PdfObject> operands, int offset) =>
            Operators.Add((operatorName, [.. operands], offset));

        public void OnInlineImage(PdfDictionary dictionary, ReadOnlyMemory<byte> data, int offset) =>
            InlineImages.Add((dictionary, data.ToArray(), offset));

        public void OnFormBegin(
            PdfDictionary formDictionary, Matrix formMatrix, PdfRectangle? boundingBox, int objectNumber,
            int offset) =>
            FormBegins.Add((formDictionary, formMatrix, boundingBox, objectNumber, offset));

        public void OnFormEnd(int objectNumber) => FormEnds.Add(objectNumber);
    }

    private static (PdfDocumentReader Reader, ContentInterpreter Interpreter, RecordingVisitor Visitor) Run(
        byte[] pdfBytes, PdfReaderOptions? options = null)
    {
        var reader = PdfReader.Open(pdfBytes, options ?? new PdfReaderOptions());
        var page = reader.GetPage(0);
        var interpreter = new ContentInterpreter(reader);
        var visitor = new RecordingVisitor();
        interpreter.Run(page, visitor);
        return (reader, interpreter, visitor);
    }

    // ── Operand types (known-answer) ────────────────────────────────────────────────────────────

    [Fact]
    public void OperandTypes_areParsedWithExactValuesAndShapes()
    {
        const string content =
            "5 w\n"
            + "-.5 6. -.5 6. re\n"
            + "(foo \\) bar (nested) baz) Tj\n"
            + "<48656C6C6F> Tj\n"
            + "/Name#20Test ri\n"
            + "[3 2] 0 d\n"
            + "[(A) -120 (B) [1 2] 5] TJ\n"
            + "/Span << /MCID 1 /Foo (bar) >> BDC\n"
            + "EMC\n"
            + "true false null \"\n";

        var (_, _, visitor) = Run(BuildPageDoc(content));

        var w = visitor.Operators.Single(o => o.Op == "w");
        Assert.Equal(5, ((PdfInteger)w.Operands[0]).Value);

        var re = visitor.Operators.Single(o => o.Op == "re");
        Assert.Equal(-0.5, ((PdfReal)re.Operands[0]).Value);
        Assert.Equal(6.0, ((PdfReal)re.Operands[1]).Value);
        Assert.Equal(-0.5, ((PdfReal)re.Operands[2]).Value);
        Assert.Equal(6.0, ((PdfReal)re.Operands[3]).Value);

        var tjCalls = visitor.Operators.Where(o => o.Op == "Tj").ToList();
        Assert.Equal(2, tjCalls.Count);
        var literal = (PdfLiteralString)tjCalls[0].Operands[0];
        Assert.Equal("foo ) bar (nested) baz", Encoding.ASCII.GetString(literal.Bytes.Span));
        var hex = (PdfHexString)tjCalls[1].Operands[0];
        Assert.Equal("Hello", Encoding.ASCII.GetString(hex.Bytes.Span));

        var ri = visitor.Operators.Single(o => o.Op == "ri");
        Assert.Equal("Name Test", ((PdfName)ri.Operands[0]).Value);

        var d = visitor.Operators.Single(o => o.Op == "d");
        var dashArray = (PdfArray)d.Operands[0];
        Assert.Equal(2, dashArray.Count);
        Assert.Equal(3, ((PdfInteger)dashArray[0]).Value);
        Assert.Equal(0, ((PdfInteger)d.Operands[1]).Value);

        var tj = visitor.Operators.Single(o => o.Op == "TJ");
        var tjArray = (PdfArray)tj.Operands[0];
        Assert.Equal(5, tjArray.Count);
        Assert.Equal("A", Encoding.ASCII.GetString(((PdfLiteralString)tjArray[0]).Bytes.Span));
        Assert.Equal(-120, ((PdfInteger)tjArray[1]).Value);
        Assert.Equal("B", Encoding.ASCII.GetString(((PdfLiteralString)tjArray[2]).Bytes.Span));
        var nested = (PdfArray)tjArray[3];
        Assert.Equal(2, nested.Count);
        Assert.Equal(1, ((PdfInteger)nested[0]).Value);
        Assert.Equal(2, ((PdfInteger)nested[1]).Value);
        Assert.Equal(5, ((PdfInteger)tjArray[4]).Value);

        var bdc = visitor.Operators.Single(o => o.Op == "BDC");
        Assert.Equal("Span", ((PdfName)bdc.Operands[0]).Value);
        var props = (PdfDictionary)bdc.Operands[1];
        Assert.Equal(1, ((PdfInteger)props.Get(new PdfName("MCID"))!).Value);

        Assert.Contains(visitor.Operators, o => o.Op == "EMC" && o.Operands.Count == 0);

        var quote = visitor.Operators.Single(o => o.Op == "\"");
        Assert.Equal(3, quote.Operands.Count);
        Assert.Same(PdfBoolean.True, quote.Operands[0]);
        Assert.Same(PdfBoolean.False, quote.Operands[1]);
        Assert.Same(PdfNull.Instance, quote.Operands[2]);
    }

    // ── BX/EX compatibility sections ────────────────────────────────────────────────────────────

    [Fact]
    public void UnknownOperator_outsideBX_isReportedOncePerPage_andInsideBX_isSilent()
    {
        // Two distinct unknown names outside BX/EX: the sink dedupes on (code, object, page), so
        // the page records the first one only.
        const string content = "Zork\nZork\nBlat\nBX\nZork\n{ pop }\n> \nEX\n";

        var (reader, _, _) = Run(BuildPageDoc(content));

        var reports = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.UnknownOperator).ToList();
        Assert.Single(reports);
        Assert.Equal(PdfReaderDiagnosticSeverity.Info, reports[0].Severity);
        Assert.Contains("'Zork'", reports[0].Message);
    }

    [Fact]
    public void CurlyBracesAndLoneGreaterThan_insideBX_doNotAbortThePage()
    {
        const string content = "BX\n{ pop } \n> \nEX\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    // ── Operand-stack and graphics-state caps ───────────────────────────────────────────────────

    [Fact]
    public void OperandStackCap_32IsOk_33IsMalformed()
    {
        // 32 numeric operands, none consumed by a real operator (so this pins the CAP itself, not
        // any one operator's own arity): the 32nd push must not itself overflow.
        var okContent = string.Join(' ', Enumerable.Repeat("1", 32));
        var overContent = string.Join(' ', Enumerable.Repeat("1", 33));

        var (okReader, _, _) = Run(BuildPageDoc(okContent));
        Assert.DoesNotContain(okReader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);

        var (overReader, _, _) = Run(BuildPageDoc(overContent));
        Assert.Contains(overReader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
    }

    [Fact]
    public void TjArrayCap_8192IsOk_8193IsMalformed()
    {
        var okArray = "[" + string.Concat(Enumerable.Repeat("0 ", 8192)) + "] TJ\n";
        var overArray = "[" + string.Concat(Enumerable.Repeat("0 ", 8193)) + "] TJ\n";

        var (okReader, _, okVisitor) = Run(BuildPageDoc(okArray));
        Assert.DoesNotContain(okReader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.Single(okVisitor.Operators, o => o.Op == "TJ");

        var (overReader, _, overVisitor) = Run(BuildPageDoc(overArray));
        Assert.Contains(overReader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.DoesNotContain(overVisitor.Operators, o => o.Op == "TJ");
    }

    [Fact]
    public void UnbalancedQ_isIgnoredWithADiagnostic()
    {
        var (reader, _, visitor) = Run(BuildPageDoc("Q\n1 w\n"));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.Contains(visitor.Operators, o => o.Op == "w"); // interpretation continued
    }

    // ── q/Q/cm matrix state, text state ──────────────────────────────────────────────────────────

    [Fact]
    public void GraphicsStateStack_savesAndRestoresTheCtm()
    {
        var interpreter = RunAndKeepInterpreter(
            BuildPageDoc("q\n2 0 0 2 10 20 cm\nQ\n"), out _);

        Assert.Equal(Matrix.Identity, interpreter.GraphicsState.Ctm);
    }

    [Fact]
    public void Cm_concatenatesOntoTheCurrentCtm()
    {
        var interpreter = RunAndKeepInterpreter(
            BuildPageDoc("2 0 0 2 10 20 cm\n"), out _);

        Assert.Equal(new Matrix(2, 0, 0, 2, 10, 20), interpreter.GraphicsState.Ctm);
    }

    [Fact]
    public void TextStateOperators_setTheExpectedFields()
    {
        const string content =
            "1 Tc 2 Tw 150 Tz 12 TL /F1 24 Tf 2 Tr 3 Ts\n"
            + "10 20 Td\n"
            + "5 -6 TD\n"
            + "1 0 0 1 100 200 Tm\n"
            + "T*\n";
        var interpreter = RunAndKeepInterpreter(
            BuildPageDoc(content, "<< /Font << /F1 5 0 R >> >>",
                new Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")),
            out _);

        Assert.Equal(1, interpreter.GraphicsState.CharSpacing);
        Assert.Equal(2, interpreter.GraphicsState.WordSpacing);
        Assert.Equal(150, interpreter.GraphicsState.HorizontalScaling);
        Assert.Equal(6, interpreter.GraphicsState.Leading); // TD's ty=-6 sets TL=-(-6)=6
        Assert.Equal("F1", ((PdfName)interpreter.GraphicsState.Font!).Value);
        Assert.Equal(24, interpreter.GraphicsState.FontSize);
        Assert.Equal(2, interpreter.GraphicsState.RenderMode);
        Assert.Equal(3, interpreter.GraphicsState.Rise);

        // After Tm replaces both matrices with [1 0 0 1 100 200], T* moves by (0, -TL=-6):
        // Tlm_new = [1 0 0 1 0 -6] x [1 0 0 1 100 200] = [1 0 0 1 100 194].
        Assert.Equal(new Matrix(1, 0, 0, 1, 100, 194), interpreter.TextState.TextMatrix);
    }

    [Fact]
    public void BT_resetsTheTextMatrices()
    {
        const string content = "1 0 0 1 100 200 Tm\nBT\n";
        var interpreter = RunAndKeepInterpreter(BuildPageDoc(content), out _);

        Assert.Equal(Matrix.Identity, interpreter.TextState.TextMatrix);
        Assert.Equal(Matrix.Identity, interpreter.TextState.TextLineMatrix);
    }

    private static ContentInterpreter RunAndKeepInterpreter(byte[] pdfBytes, out PdfDocumentReader reader)
    {
        reader = PdfReader.Open(pdfBytes);
        var interpreter = new ContentInterpreter(reader);
        interpreter.Run(reader.GetPage(0), new RecordingVisitor());
        return interpreter;
    }

    // ── gs with /Font ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gs_withFont_surfacesTheFontSelectionToTheState()
    {
        var interpreter = RunAndKeepInterpreter(
            BuildPageDoc(
                "/G1 gs\n",
                "<< /ExtGState << /G1 6 0 R >> >>",
                new Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
                new Obj(6, "<< /Type /ExtGState /Font [5 0 R 18] >>")),
            out _);

        var fontRef = Assert.IsType<PdfIndirectReference>(interpreter.GraphicsState.Font);
        Assert.Equal(5, fontRef.ObjectNumber);
        Assert.Equal(18, interpreter.GraphicsState.FontSize);
    }

    [Fact]
    public void Gs_namingAMissingExtGState_reportsResourceMissing()
    {
        var (reader, _, _) = Run(BuildPageDoc("/Absent gs\n", "<< /ExtGState << >> >>"));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
    }

    // ── Form XObjects ────────────────────────────────────────────────────────────────────────────

    private static Obj[] BuildFormChain(int count, string leafExtra = "")
    {
        // Form N invokes Form N+1; Form `count` is the one final recursion is expected to skip.
        var objs = new List<Obj>();
        for (var i = 1; i <= count; i++)
        {
            var body = i < count ? $"/F{i + 1} Do" : leafExtra;
            var resources = i < count
                ? $"<< /XObject << /F{i + 1} {11 + i} 0 R >> >>"
                : "<< >>";
            objs.Add(new Obj(
                10 + i,
                $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources {resources} >>",
                Encoding.ASCII.GetBytes(body)));
        }
        return [.. objs];
    }

    [Fact]
    public void NestedForms_toDepth32_ok_33_reportsDepthExceeded()
    {
        var forms = BuildFormChain(33);
        var doc = BuildPageDoc("/F1 Do\n", "<< /XObject << /F1 11 0 R >> >>", forms);

        var (reader, _, visitor) = Run(doc);

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FormXObjectDepthExceeded);
        Assert.Equal(33, visitor.Operators.Count(o => o.Op == "Do"));
        Assert.Equal(32, visitor.FormBegins.Count);
        Assert.Equal(32, visitor.FormEnds.Count);
    }

    [Fact]
    public void NestedForms_atDepth32_reportsNoDepthExceeded()
    {
        var forms = BuildFormChain(32);
        var doc = BuildPageDoc("/F1 Do\n", "<< /XObject << /F1 11 0 R >> >>", forms);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FormXObjectDepthExceeded);
        Assert.Equal(32, visitor.FormBegins.Count);
    }

    [Fact]
    public void SelfReferencingForm_reportsCycleOnce()
    {
        var doc = BuildPageDoc(
            "/F1 Do\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(
                11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] "
                + "/Resources << /XObject << /F1 11 0 R >> >> >>",
                "/F1 Do"u8.ToArray()));

        var (reader, _, visitor) = Run(doc);

        var cycles = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.FormXObjectCycle).ToList();
        Assert.Single(cycles);
        Assert.Single(visitor.FormBegins);
        Assert.Single(visitor.FormEnds);
    }

    [Fact]
    public void FormDrawn4097Times_reportsBudgetExceeded_andThePageStillCompletes()
    {
        var pageContent = string.Concat(Enumerable.Repeat("/F1 Do\n", 4097)) + "1 w\n";
        var doc = BuildPageDoc(
            pageContent, "<< /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", []));

        var (reader, _, visitor) = Run(doc);

        var budget = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.FormXObjectBudgetExceeded).ToList();
        Assert.Single(budget);
        Assert.Equal(4097, visitor.Operators.Count(o => o.Op == "Do"));
        Assert.True(visitor.FormBegins.Count <= 4096);
        Assert.Contains(visitor.Operators, o => o.Op == "w"); // the page itself still finished
    }

    [Fact]
    public void Form_withoutOwnResources_fallsBackToTheParentsResources()
    {
        var doc = BuildPageDoc(
            "/F1 Do\n", "<< /Shading << /Sh1 20 0 R >> /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", "/Sh1 sh"u8.ToArray()),
            new Obj(20, "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 1] >>"));

        var (reader, _, _) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
    }

    [Fact]
    public void Form_matrixAndBBox_areHandedToTheVisitor()
    {
        var doc = BuildPageDoc(
            "/F1 Do\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(
                11,
                "<< /Type /XObject /Subtype /Form /BBox [1 2 3 4] /Matrix [2 0 0 2 5 6] >>",
                []));

        var (_, _, visitor) = Run(doc);

        var begin = Assert.Single(visitor.FormBegins);
        Assert.Equal(new Matrix(2, 0, 0, 2, 5, 6), begin.Matrix);
        Assert.NotNull(begin.BBox);
        Assert.Equal(1, begin.BBox!.LlX);
        Assert.Equal(2, begin.BBox.LlY);
        Assert.Equal(3, begin.BBox.UrX);
        Assert.Equal(4, begin.BBox.UrY);
        Assert.Equal(11, begin.ObjectNumber);
    }

    // ── Inline images ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnfilteredGray8Bit_computedLength_skipsEmbeddedEiBytes()
    {
        byte[] pixelData = [0x45, 0x49, 0x10, 0x20]; // literally contains 'E','I'
        var content = BuildInlineImageContent(
            "/W 2 /H 2 /BPC 8 /CS /G", pixelData, trailingOperator: "Q");

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(pixelData));
        Assert.Equal(2, ((PdfInteger)img.Dict.Get(new PdfName("Width"))!).Value);
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void L_isHonoured()
    {
        byte[] data = "ABCDEFGH"u8.ToArray();
        var content =
            "BI /W 2 /H 2 /BPC 8 /CS /RGB /F /AHx /L " + data.Length + " ID "
            + Encoding.ASCII.GetString(data) + " EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void L_pastTheEnd_reportsMalformed_andRecoversViaTheEiScan()
    {
        byte[] data = "ABCDEFGH"u8.ToArray();
        var content =
            "BI /F /AHx /L 999999 ID "
            + Encoding.ASCII.GetString(data) + " EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void FilteredAHx_usesTheEiScan()
    {
        byte[] data = "0123456789ABCDEF"u8.ToArray();
        var content = "BI /F /AHx ID " + Encoding.ASCII.GetString(data) + " EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void DctInlineImage_withAFalseEiInsideItsData_isSkippedByTheScan()
    {
        // A whitespace-delimited "EI" followed by an unterminated literal string, which never
        // closes anywhere in the rest of the content stream. The resync probe's lex attempt from
        // that point throws, so ScanForEi rejects it and keeps looking for the real one.
        var falseCandidate = " EI (unterminated "u8.ToArray();
        byte[] jpegNoise = [0x01, 0x02, 0xFF, 0xD8, 0xFF];
        var data = jpegNoise.Concat(falseCandidate).Concat(new byte[] { 3, 4, 5 }).ToArray();

        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void DctInlineImage_withAFalseEiFollowedByBinaryNoise_isSkippedByTheScan()
    {
        // The harder false candidate: " EI " followed by bytes outside ISO 32000-2 §7.2.2's
        // whitespace and delimiter sets, which the lexer accepts as one Keyword token without
        // throwing. Only the probe's operator check (the keyword is not in Annex A Table A.1)
        // rejects this one; the real EI follows and is accepted.
        var falseCandidate = " EI "u8.ToArray();
        byte[] noiseAfter = [0x8F, 0x12, 0xC4, 0x7A, 0x20, 0xFE, 0x01];
        byte[] jpegNoise = [0xFF, 0xD8, 0xFF, 0xE0, 0x00];
        var data = jpegNoise.Concat(falseCandidate).Concat(noiseAfter).ToArray();

        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Equal("Q", Assert.Single(visitor.Operators).Op);
    }

    [Fact]
    public void AbbreviationExpansion_isPinned()
    {
        var content = "BI /W 2 /H 2 /BPC 8 /CS /G /F [/AHx /Fl] ID XYZDATA1234 EI\nQ\n";

        var (_, _, visitor) = Run(BuildPageDoc(content));

        var img = Assert.Single(visitor.InlineImages);
        Assert.Equal(2, ((PdfInteger)img.Dict.Get(new PdfName("Width"))!).Value);
        Assert.Equal(
            "DeviceGray", ((PdfName)img.Dict.Get(PdfName.ColorSpace)!).Value);
        var filters = (PdfArray)img.Dict.Get(PdfName.Filter)!;
        Assert.Equal("ASCIIHexDecode", ((PdfName)filters[0]).Value);
        Assert.Equal("FlateDecode", ((PdfName)filters[1]).Value);
    }

    [Fact]
    public void NamedColorSpace_resolvesThroughResourcesColorSpace()
    {
        var content = "BI /W 1 /H 1 /BPC 8 /CS /MyCS ID X EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(
            content, "<< /ColorSpace << /MyCS [/Indexed /DeviceRGB 0 (\\000\\000\\000)] >> >>"));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
        Assert.Single(visitor.InlineImages);
    }

    [Fact]
    public void NamedColorSpace_whenAbsentFromResources_reportsResourceMissing()
    {
        var content = "BI /W 1 /H 1 /BPC 8 /CS /MyCS ID X EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
        Assert.Single(visitor.InlineImages); // still delimited via the EI scan fallback
    }

    [Fact]
    public void JpxDecodeInlineImage_reportsMalformed_andTheStreamContinuesAfterIt()
    {
        var content = "BI /W 1 /H 1 /BPC 8 /F /JPXDecode ID XYZ EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        Assert.Empty(visitor.InlineImages);
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void MissingId_reportsMalformed()
    {
        var (reader, _, visitor) = Run(BuildPageDoc("BI /W 1 /H 1"));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        Assert.Empty(visitor.InlineImages);
    }

    private static string BuildInlineImageContent(
        string keys, byte[] data, string trailingOperator)
    {
        var sb = new StringBuilder();
        sb.Append("BI ").Append(keys).Append(" ID ");
        var prefix = sb.ToString();
        var suffix = " EI\n" + trailingOperator + "\n";
        return prefix + Encoding.Latin1.GetString(data) + suffix;
    }

    // ── /Contents array concatenation ────────────────────────────────────────────────────────────

    [Fact]
    public void TokenSplitAcrossStreams_isNotGlued()
    {
        var doc = BuildPdf(
            1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents [4 0 R 5 0 R] >>"),
            new Obj(4, "<< >>", "q\nBT"u8.ToArray()), // ends mid-token, no trailing whitespace
            new Obj(5, "<< >>", "ET\nQ"u8.ToArray())); // begins with no leading whitespace

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnknownOperator);
        Assert.Equal(["q", "BT", "ET", "Q"], visitor.Operators.Select(o => o.Op));
    }

    [Fact]
    public void NonStreamContentsElement_isSkippedWithLexError()
    {
        var doc = BuildPdf(
            1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents [5 0 R 6 0 R] >>"),
            new Obj(5, "42"), // not a stream at all
            new Obj(6, "<< >>", "1 w"u8.ToArray()));

        var (reader, _, visitor) = Run(doc);

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        Assert.Contains(visitor.Operators, o => o.Op == "w"); // object 6's content still ran
    }

    [Fact]
    public void ContentsStreamWithAnImageFilter_reportsLexError()
    {
        var doc = BuildPdf(
            1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents 4 0 R >>"),
            new Obj(4, "<< /Filter /DCTDecode >>", [0xFF, 0xD8, 0xFF, 0xD9]));

        var (reader, _, visitor) = Run(doc);

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        Assert.Empty(visitor.Operators);
    }

    // ── 64 MiB content cap ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContentExceeding64MiB_reportsTooLarge_andKeepsTheOperatorsBeforeTheCap()
    {
        var unit = "0 0 1 1 re\n"u8.ToArray();
        var repeats = (67 * 1024 * 1024 / unit.Length) + 200_000;
        var raw = new byte[unit.Length * repeats];
        for (var i = 0; i < repeats; i++)
            unit.CopyTo(raw, i * unit.Length);

        var doc = BuildPdf(
            1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents 4 0 R >>"),
            new Obj(4, "<< /Filter /FlateDecode >>", Flate(raw)));

        var (reader, _, visitor) = Run(doc);

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamTooLarge);
        Assert.NotEmpty(visitor.Operators);
        Assert.True(visitor.Operators.Count < repeats);
        Assert.All(visitor.Operators, o =>
        {
            Assert.Equal("re", o.Op);
            Assert.Equal(4, o.Operands.Count);
        });
    }

    // ── Fuzzing ──────────────────────────────────────────────────────────────────────────────────

    private static readonly byte[] FuzzSeed = BuildPdf(
        1,
        new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new Obj(3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 6 0 R >> /XObject << /X1 7 0 R >> "
            + "/ExtGState << /G1 8 0 R >> >> /Contents 4 0 R >>"),
        new Obj(4, "<< >>", Encoding.ASCII.GetBytes(
            "q\n2 0 0 2 10 20 cm\nBT\n/F1 12 Tf\n(Hi) Tj\n[(A) -10 (B)] TJ\nET\n"
            + "/G1 gs\n/X1 Do\nBI /W 2 /H 2 /BPC 8 /CS /G ID \x01\x02\x03\x04 EI\nQ\n")),
        new Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        new Obj(
            7, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Matrix [1 0 0 1 0 0] >>",
            "1 w\n"u8.ToArray()),
        new Obj(8, "<< /Type /ExtGState /Font [6 0 R 10] >>"));

    private readonly record struct MutationOp(int Kind, int Position, byte Value, int Length);

    private static readonly Gen<MutationOp> MutationOpGen =
        Gen.Select(
            Gen.Int[0, 5], Gen.Int[0, int.MaxValue], Gen.Byte, Gen.Int[1, 32],
            (kind, position, value, length) => new MutationOp(kind, position, value, length));

    private static readonly Gen<byte[]> FuzzInputGen =
        MutationOpGen.Array[1, 8].Select(ops => Mutate(FuzzSeed, ops));

    private static byte[] Mutate(byte[] seed, MutationOp[] ops)
    {
        var buffer = new List<byte>(seed);
        foreach (var op in ops)
        {
            if (buffer.Count == 0) { buffer.Add(op.Value); continue; }
            var position = op.Position % buffer.Count;
            switch (op.Kind)
            {
                case 0: buffer[position] ^= (byte)(1 << (op.Value % 8)); break;
                case 1: buffer[position] = op.Value; break;
                case 2: buffer.RemoveAt(position); break;
                case 3: buffer.Insert(position, op.Value); break;
                case 4:
                    var length = Math.Min(op.Length, buffer.Count - position);
                    if (length > 0 && buffer.Count + length <= 1 << 20)
                        buffer.InsertRange(position, buffer.GetRange(position, length));
                    break;
                case 5:
                    var cut = position + 1;
                    if (cut < buffer.Count)
                        buffer.RemoveRange(cut, buffer.Count - cut);
                    break;
            }
            if (buffer.Count > 1 << 20)
                buffer.RemoveRange(1 << 20, buffer.Count - (1 << 20));
        }
        return buffer.Count == 0 ? [0] : [.. buffer];
    }

    [Fact]
    public void Fuzz_run_neverThrowsOutsideTheDeclaredVocabulary_andAlwaysTerminates()
        => FuzzInputGen.Sample(AssertInterpreterIsRobust, iter: FuzzBudget.Iterations);

    private static void AssertInterpreterIsRobust(byte[] bytes)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var reader = PdfReader.Open(bytes, new PdfReaderOptions
            {
                MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes,
            });
            if (reader.PageCount == 0)
                return;
            var interpreter = new ContentInterpreter(reader);
            interpreter.Run(reader.GetPage(0), new RecordingVisitor());
        }
        catch (Exception ex) when (ex is InvalidDataException or UnsupportedPdfFeatureException or PdfPasswordException)
        {
            // Acceptable outcome; see ParserFuzzTests' own class doc for the same policy this
            // interpreter follows: a robustness oracle, not a conformance one.
        }
        Assert.True(
            stopwatch.Elapsed <= TimeSpan.FromSeconds(4),
            $"content interpretation took {stopwatch.Elapsed} on a {bytes.Length}-byte input.");
    }

    private static class FuzzBudget
    {
        private const long DefaultIterations = 3_000;

        internal static long Iterations
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("VELLUMPDF_FUZZ_ITER");
                return long.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultIterations;
            }
        }
    }
}
