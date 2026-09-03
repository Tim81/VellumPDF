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
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, reports[0].Severity);
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

    [Fact]
    public void UnknownOperatorInsideBX_dropsItsOperands_perTable33()
    {
        // Table 33: "Unrecognised operators (along with their operands) shall be ignored without
        // error until the balancing EX operator is encountered" (#402). Before the fix, the
        // unknown-operator branch never cleared the operand stack regardless of _bxDepth, so the
        // 1 2 3 preceding SomeFutureOp survived to be misread as w's own operand.
        const string content = "BX\n1 2 3 SomeFutureOp\n5 w\nEX\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Equal(["BX", "w", "EX"], visitor.Operators.Select(o => o.Op));
        var w = visitor.Operators.Single(o => o.Op == "w");
        Assert.Equal(5, ((PdfInteger)w.Operands[0]).Value);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnknownOperator);
    }

    // ── Operand-stack and graphics-state caps ───────────────────────────────────────────────────

    [Fact]
    public void OperandStackCap_64IsOk_65IsALimitNotAMalformation()
    {
        // 64 numeric operands, none consumed by an operator (so this pins the CAP itself, not any
        // one operator's own arity): the 64th push must not itself overflow. 64, not 32:
        // Table 73's scn operator can legally take 33+ operands for a DeviceN space with many
        // colourants (see the cap's own comment in ContentInterpreter), so 32 rejected a legal call.
        var okContent = string.Join(' ', Enumerable.Repeat("1", 64));
        var overContent = string.Join(' ', Enumerable.Repeat("1", 65));

        var (okReader, _, _) = Run(BuildPageDoc(okContent));
        Assert.DoesNotContain(okReader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);

        var (overReader, _, _) = Run(BuildPageDoc(overContent));
        // This reader's own ceiling, not a producer-side malformation: ContentLimitExceeded (#402),
        // not OperandStackMalformed.
        Assert.Contains(overReader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.DoesNotContain(overReader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
    }

    [Fact]
    public void TjArrayCap_8192IsOk_8193IsALimitNotAMalformation()
    {
        var okArray = "[" + string.Concat(Enumerable.Repeat("0 ", 8192)) + "] TJ\n";
        var overArray = "[" + string.Concat(Enumerable.Repeat("0 ", 8193)) + "] TJ\n";

        var (okReader, _, okVisitor) = Run(BuildPageDoc(okArray));
        Assert.DoesNotContain(okReader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.Single(okVisitor.Operators, o => o.Op == "TJ");

        var (overReader, _, overVisitor) = Run(BuildPageDoc(overArray));
        Assert.Contains(overReader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.DoesNotContain(overReader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.DoesNotContain(overVisitor.Operators, o => o.Op == "TJ");
    }

    [Fact]
    public void TjOperand_notAnArray_reportsOperandStackMalformed_notALimit()
    {
        // A wrong TYPE (a producer-side malformation) must stay OperandStackMalformed even though
        // the ELEMENT-COUNT cap right beside it in the same switch case moved to ContentLimitExceeded.
        var (reader, _, visitor) = Run(BuildPageDoc("5 TJ\n1 w\n"));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.DoesNotContain(visitor.Operators, o => o.Op == "TJ");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
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

    [Fact]
    public void Form_matrixAndBBox_resolveThroughAnIndirectReference()
    {
        // §7.3.10 lets any dictionary entry be given as an indirect reference; Table 93 gives
        // /Matrix and /BBox no direct-only restriction, so both must resolve before the shape
        // check ContentInterpreter runs against them (#402).
        var doc = BuildPageDoc(
            "/F1 Do\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(
                11, "<< /Type /XObject /Subtype /Form /BBox 12 0 R /Matrix 13 0 R >>", []),
            new Obj(12, "[1 2 3 4]"),
            new Obj(13, "[2 0 0 2 5 6]"));

        var (_, _, visitor) = Run(doc);

        var begin = Assert.Single(visitor.FormBegins);
        Assert.Equal(new Matrix(2, 0, 0, 2, 5, 6), begin.Matrix);
        Assert.NotNull(begin.BBox);
        Assert.Equal(1, begin.BBox!.LlX);
        Assert.Equal(4, begin.BBox.UrY);
    }

    // ── Do brackets a form's content in an implicit q/Q (ISO 32000-2 §8.10.1) ───────────────────

    [Fact]
    public void Do_onAFormThatChangesTheCtm_doesNotLeakTheChangeIntoTheInvoker()
    {
        var doc = BuildPageDoc(
            "/F1 Do\n1 w\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(
                11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>",
                "3 0 0 3 0 0 cm"u8.ToArray()));

        var interpreter = RunAndKeepInterpreter(doc, out var reader);

        Assert.Equal(Matrix.Identity, interpreter.GraphicsState.Ctm);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
    }

    [Fact]
    public void Do_onAFormWithUnbalancedQ_doesNotPopThePagesOwnSave()
    {
        var doc = BuildPageDoc(
            "q\n/F1 Do\nQ\n1 w\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(
                11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", "Q Q Q"u8.ToArray()));

        var (reader, _, visitor) = Run(doc);

        // The form's three stray 'Q's each report against the form's own object number, 11, and
        // the page's own 'q'/'Q' pairing (opened before Do, closed after it) stays untouched: the
        // page's own Q must not itself be reported as unbalanced.
        var malformed = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed).ToList();
        Assert.NotEmpty(malformed);
        Assert.All(malformed, d => Assert.Equal(11, d.ObjectNumber));
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void Do_onAQOnlyForm_drawn70Times_producesNoGraphicsStateDepthDiagnostic()
    {
        var pageContent = string.Concat(Enumerable.Repeat("/F1 Do\n", 70)) + "1 w\n";
        var doc = BuildPageDoc(
            pageContent, "<< /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", "q\n"u8.ToArray()));

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void Do_onAFormWithAStrayEmcAndUnbalancedBX_leavesThePageStateBalanced()
    {
        var doc = BuildPageDoc(
            "BDC\n/F1 Do\nEMC\nZorkAfterDo\n",
            "<< /XObject << /F1 12 0 R >> >>",
            new Obj(
                11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", "EMC"u8.ToArray()),
            new Obj(
                12, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] "
                + "/Resources << /XObject << /F1 11 0 R >> >> >>",
                "/Span << >> BDC\n/F1 Do\n"u8.ToArray()));

        var (reader, _, visitor) = Run(doc);

        // The innermost form's stray EMC is reported against ITS OWN object number (11), and the
        // page's own BDC (opened before Do, closed by the page's own EMC after it) still balances:
        // no diagnostic against the page (null object number) for an unmatched EMC.
        var emcReports = reader.Diagnostics
            .Where(d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed
                && d.Message.Contains("EMC", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(emcReports, d => d.ObjectNumber == 11);
        Assert.DoesNotContain(emcReports, d => d.ObjectNumber is null);

        // The middle form's unbalanced BX leaves the PAGE outside a compatibility section once Do
        // returns (the floor resets _bxDepth back to 0, not to "still inside BX"), so the page's
        // own unknown operator right after Do is reported rather than silently swallowed.
        Assert.Contains(
            reader.Diagnostics,
            d => d.Code == PdfReaderDiagnosticCode.UnknownOperator
                && d.Message.Contains("ZorkAfterDo", StringComparison.Ordinal));
    }

    // ── The 64 MiB content budget covers Form XObject invocations too (#402) ───────────────────

    [Fact]
    public void FormDrawnRepeatedly_countsTowardTheSameContentBudgetAsThePage()
    {
        // A form whose own decoded content is ~20 MiB, drawn 4 times: well past the combined
        // 64 MiB page-and-forms budget on the 4th invocation, without any single form or the
        // page's own /Contents alone being anywhere near the cap.
        var unit = "0 0 1 1 re\n"u8.ToArray();
        var repeatsPerForm = (20 * 1024 * 1024) / unit.Length;
        var formBody = new byte[unit.Length * repeatsPerForm];
        for (var i = 0; i < repeatsPerForm; i++)
            unit.CopyTo(formBody, i * unit.Length);

        var pageContent = string.Concat(Enumerable.Repeat("/F1 Do\n", 4)) + "1 w\n";
        var doc = BuildPageDoc(
            pageContent, "<< /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", formBody));

        var (reader, _, visitor) = Run(doc);

        var tooLarge = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.ContentStreamTooLarge).ToList();
        Assert.Single(tooLarge);

        // Expected operator count computed from the byte budget, not read off the output: the
        // whole 64 MiB budget divided by one form's own byte length gives how many WHOLE forms
        // fit, each contributing repeatsPerForm 're' operators (the page's own /Contents is a few
        // bytes and negligible against a 64 MiB budget).
        var wholeFormsThatFit = (int)(ContentInterpreterBudget.MaxContentBytes / formBody.Length);
        var reOps = visitor.Operators.Where(o => o.Op == "re").ToList();
        Assert.True(reOps.Count >= wholeFormsThatFit * repeatsPerForm);
        Assert.True(reOps.Count < 4 * repeatsPerForm);
        Assert.Contains(visitor.Operators, o => o.Op == "w"); // the page's own content after the last Do still ran
    }

    // Mirrors ContentInterpreter's own private MaxContentBytes so the test above can compute an
    // expected operator count from the budget rather than reading it off the interpreter's output.
    private static class ContentInterpreterBudget
    {
        internal const long MaxContentBytes = 64L * 1024 * 1024;
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
    public void NegativeL_reportsMalformed_andRecoversViaTheEiScan()
    {
        byte[] data = "ABCDEFGH"u8.ToArray();
        var content = "BI /F /AHx /L -1 ID " + Encoding.ASCII.GetString(data) + " EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Theory]
    [InlineData("4294967298")] // outside int's range
    [InlineData("2.9")] // a PdfReal, not a PdfInteger (Table 87 types /W as integer)
    public void InvalidW_takesTheEiScanPath_withTheMissingOrInvalidReport(string invalidWidth)
    {
        byte[] data = "ABCD"u8.ToArray();
        var content = $"BI /W {invalidWidth} /H 2 /BPC 8 /CS /G ID "
            + Encoding.ASCII.GetString(data) + " EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(
            reader.Diagnostics,
            d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed
                && d.Message.Contains("missing, or carries an invalid", StringComparison.Ordinal));
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
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

    // ── The bounded resync probe (#402): linear-time scan, less strict, still rejects noise ────

    [Fact]
    public void ManyFalseEiCandidates_doesNotThrow_andReportsADiagnostic()
    {
        // Reproduces the pre-#402 quadratic blowup: N repeats of " EI (" (a false candidate
        // followed by the start of a literal string) after a DCT-filtered image's own data. The
        // unbounded probe this used to run from EVERY candidate re-lexed all the way to the end of
        // the buffer looking for the string's own closing ')', which never comes; O(N) work per
        // candidate made the whole scan O(N^2) (measured pre-fix: 100 KB content, 18 s; 400 KB,
        // 305 s, from a 1.2 KB Flate-compressed source). The bounded probe caps the per-candidate
        // cost, so this reads (with `dotnet test`'s own default timeout as the actual regression
        // guard, per this repo's no-wall-clock-assertion rule) rather than hanging.
        const int n = 20_000;
        var falseCandidate = " EI ("u8.ToArray();
        var noise = new byte[falseCandidate.Length * n];
        for (var i = 0; i < n; i++)
            falseCandidate.CopyTo(noise, i * falseCandidate.Length);

        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat<byte>([0xFF, 0xD8, 0xFF])
            .Concat(noise)
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, _) = Run(doc);

        Assert.Contains(
            reader.Diagnostics,
            d => d.Code is PdfReaderDiagnosticCode.ContentStreamLexError
                or PdfReaderDiagnosticCode.InlineImageMalformed);
    }

    [Fact]
    public void FalseEiCandidate_followedByAnUnknownButPrintableOperator_isAccepted()
    {
        // 'PS' is an unrecognised-but-printable operator name (§7.8.2 tolerates it outside
        // BX/EX); the probe accepts it as a plausible operator rather than rejecting the whole
        // candidate on account of it, so the false EI right after the image data still resolves
        // correctly, and 'PS' itself reaches the ordinary unknown-operator path once the
        // interpreter's own main loop gets there.
        byte[] data = [0xFF, 0xD8, 0xFF];
        var content = "q\nBI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat(" EI PS Q 1 w\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(
            reader.Diagnostics,
            d => d.Code == PdfReaderDiagnosticCode.UnknownOperator
                && d.Message.Contains("'PS'", StringComparison.Ordinal));
        Assert.Equal(["q", "Q", "w"], visitor.Operators.Select(o => o.Op));
    }

    [Fact]
    public void FalseEiCandidate_followedByABxExSection_isAccepted()
    {
        byte[] data = [0xFF, 0xD8, 0xFF];
        var content = "q\nBI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat(" EI BX { } EX Q 1 w\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void TwoConsecutiveInlineImages_theSecondWithAShortDictionary_bothDelimitCorrectly()
    {
        byte[] data1 = [0x10, 0x20, 0x30, 0x40]; // no 'E'/'I' bytes: unambiguous EI scan
        byte[] data2 = [0x99]; // /H is absent, so this one falls to the EI scan too
        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(data1).Concat(" EI\n"u8.ToArray())
            .Concat("BI /IM true /W 8 ID "u8.ToArray())
            .Concat(data2).Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.Equal(2, visitor.InlineImages.Count);
        Assert.True(visitor.InlineImages[0].Data.AsSpan().SequenceEqual(data1));
        Assert.True(visitor.InlineImages[1].Data.AsSpan().SequenceEqual(data2));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void FalseEiCandidate_followedByALongLiteralStringStraddlingTheProbeWindow_isAccepted()
    {
        // The probe's own bounded lexer runs off its 128-byte window mid-string here (the literal
        // is 200 bytes, longer than the window), the inconclusive case this reader accepts rather
        // than rejects: PdfLexer's string readers advance Position as they read, so reaching the
        // window's own end here means the window ran out, not that the bytes were malformed.
        var longLiteral = new string('X', 200);
        byte[] data = [0xFF, 0xD8, 0xFF];
        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat(Encoding.ASCII.GetBytes($" EI ({longLiteral}) Tj\nQ\n"))
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        var tj = Assert.Single(visitor.Operators, o => o.Op == "Tj");
        var str = (PdfLiteralString)tj.Operands[0];
        Assert.Equal(longLiteral, Encoding.ASCII.GetString(str.Bytes.Span));
    }

    // ── A failed tier-a/tier-b end falls back to the scan instead of losing the stream (#402) ──

    [Fact]
    public void L_oneShort_reportsMalformed_andRecoversViaTheEiScan()
    {
        byte[] data = "ABCD"u8.ToArray();
        var content = $"BI /F /AHx /L {data.Length - 1} ID "
            + Encoding.ASCII.GetString(data) + " EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void IdFollowedByCrLf_unfilteredImage_treatsBothBytesAsOneSeparator()
    {
        var content = "BI /W 2 /H 2 /BPC 8 /CS /G ID\r\nABCD EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual("ABCD"u8.ToArray()));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void IdFollowedByCrLf_withExplicitL_treatsBothBytesAsOneSeparator()
    {
        var content = "BI /F /AHx /L 4 ID\r\nABCD EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual("ABCD"u8.ToArray()));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void W_zero_reportsMalformed_andRecoversViaTheEiScan()
    {
        var content = "BI /W 0 /H 2 /BPC 8 /CS /G ID ABCD EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual("ABCD"u8.ToArray()));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    // ── Table 92 abbreviations inside a /CS array (#402) ────────────────────────────────────────

    [Fact]
    public void CsArray_tableNinetyTwoAbbreviationsExpand_andIndexedComponentCountDrivesTierB()
    {
        // §8.9.7's one composite inline colour space: [/I baseSpace hival lookup]. /I and /RGB
        // (the array's first two elements) are Table 92 abbreviations; 1 and the hex string are
        // left alone. An array whose first element is /Indexed counts one component regardless of
        // the base space (§8.6.6.3: an Indexed sample is always a single index value), so tier b
        // computes the data length without resolving /DeviceRGB's own component count at all. A
        // literal " EI x" is embedded inside the tier-b-computed 5-byte data window: if this
        // regressed to the EI scan (tier c) instead, that decoy would be mistaken for the
        // terminator, and the image would come out 1 byte long instead of 5.
        var content = "BI /W 5 /H 1 /BPC 8 /CS [/I /RGB 1 <000000FFFFFF>] ID  EI x EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        var img = Assert.Single(visitor.InlineImages);
        var csArray = (PdfArray)img.Dict.Get(PdfName.ColorSpace)!;
        Assert.Equal(4, csArray.Count);
        Assert.Equal("Indexed", ((PdfName)csArray[0]).Value);
        Assert.Equal("DeviceRGB", ((PdfName)csArray[1]).Value);
        Assert.Equal(1, ((PdfInteger)csArray[2]).Value);
        Assert.True(img.Data.AsSpan().SequenceEqual(" EI x"u8.ToArray()));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
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

    [Fact]
    public void ContentExceeding64MiB_acrossTwoStreams_appendsTheSecondStreamAfterTheFirst_notOverIt()
    {
        // A regression pin for a wrong Array.Copy overload (#402): copying the truncated tail
        // chunk to index 0 of the capped buffer, rather than to `written`, silently overwrote the
        // FIRST stream's own already-copied bytes once a second stream needed truncating. The
        // single-stream cap test above cannot catch this: it never has anything already written
        // when the truncation branch runs (`written == 0` there), so the wrong overload and the
        // right one produce identical output in that one-stream case.
        var unitA = "0 0 1 1 re\n"u8.ToArray();
        var repeatsA = 40 * 1024 * 1024 / unitA.Length;
        var rawA = new byte[unitA.Length * repeatsA];
        for (var i = 0; i < repeatsA; i++)
            unitA.CopyTo(rawA, i * unitA.Length);

        var unitB = "1 w\n"u8.ToArray();
        var repeatsB = 30 * 1024 * 1024 / unitB.Length;
        var rawB = new byte[unitB.Length * repeatsB];
        for (var i = 0; i < repeatsB; i++)
            unitB.CopyTo(rawB, i * unitB.Length);

        var doc = BuildPdf(
            1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents [4 0 R 5 0 R] >>"),
            new Obj(4, "<< /Filter /FlateDecode >>", Flate(rawA)),
            new Obj(5, "<< /Filter /FlateDecode >>", Flate(rawB)));

        var (reader, _, visitor) = Run(doc);

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamTooLarge);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);

        var reOps = visitor.Operators.TakeWhile(o => o.Op == "re").ToList();
        Assert.Equal(repeatsA, reOps.Count);
        Assert.All(reOps, o => Assert.Equal(4, o.Operands.Count));

        var rest = visitor.Operators.Skip(reOps.Count).ToList();
        Assert.NotEmpty(rest);
        Assert.True(rest.Count < repeatsB);
        Assert.All(rest, o =>
        {
            Assert.Equal("w", o.Op);
            Assert.Single(o.Operands);
        });
    }

    // ── ContentStreamTooLarge and FormXObjectBudgetExceeded use ReportRetained (#402) ───────────

    [Fact]
    public void ContentStreamTooLarge_isRetainedEvenOnceMaxDiagnosticsIsAlreadySpent()
    {
        // Two pages: page 0 spends the whole cap (MaxDiagnostics = 1) on an ordinary UnknownOperator
        // report; page 1's /Contents then exceeds the 64 MiB budget. Without ReportRetained, that
        // second report would be silently dropped in favour of the DiagnosticsSuppressed sentinel,
        // since the sink's ordinary cap (shared across the whole reader, not per page) is already
        // full by the time page 1 is interpreted (#398 set this rule for PageTreeWalker; this is
        // the content-interpreter counterpart).
        var unit = "0 0 1 1 re\n"u8.ToArray();
        var repeats = (67 * 1024 * 1024 / unit.Length) + 200_000;
        var raw = new byte[unit.Length * repeats];
        for (var i = 0; i < repeats; i++)
            unit.CopyTo(raw, i * unit.Length);

        var doc = BuildPdf(
            1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>"),
            new Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "Zork\n"u8.ToArray()),
            new Obj(6,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents 7 0 R >>"),
            new Obj(7, "<< /Filter /FlateDecode >>", Flate(raw)));

        var reader = PdfReader.Open(doc, new PdfReaderOptions { MaxDiagnostics = 1 });
        var interpreter = new ContentInterpreter(reader);

        interpreter.Run(reader.GetPage(0), new RecordingVisitor());
        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnknownOperator);

        interpreter.Run(reader.GetPage(1), new RecordingVisitor());
        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamTooLarge);
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
        // Two separate try blocks, not one covering Open/GetPage/Run together: ContentInterpreter's
        // own class doc promises InvalidDataException never escapes Run (UnsupportedPdfFeatureException
        // is the one exception allowed to). One try block covering all three would make a Run-time
        // InvalidDataException indistinguishable from an open-time one, silently accepting a
        // regression in that promise as just another "acceptable outcome".
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        PdfDocumentReader? reader = null;
        PdfReadPage? page = null;
        try
        {
            reader = PdfReader.Open(bytes, new PdfReaderOptions
            {
                MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes,
            });
            if (reader.PageCount != 0)
                page = reader.GetPage(0);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnsupportedPdfFeatureException or PdfPasswordException)
        {
            // Acceptable outcome; see ParserFuzzTests' own class doc for the same policy this
            // interpreter follows: a robustness oracle, not a conformance one.
        }

        if (page is not null)
        {
            try
            {
                var interpreter = new ContentInterpreter(reader!);
                interpreter.Run(page, new RecordingVisitor());
            }
            catch (UnsupportedPdfFeatureException)
            {
                // The one exception Run's own class doc allows to propagate.
            }
        }

        reader?.Dispose();
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
