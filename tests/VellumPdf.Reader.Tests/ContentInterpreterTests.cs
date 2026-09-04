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
            // 'sc' (Table 73) takes a variable operand count and forwards it to the visitor
            // untouched (#402 round 4: moved off '\"', once '\"' gained its own Table 107 operand
            // type check, so this line still pins boolean/null keyword parsing on an operator that
            // does not type-check them).
            + "true false null sc\n";

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

        var sc = visitor.Operators.Single(o => o.Op == "sc");
        Assert.Equal(3, sc.Operands.Count);
        Assert.Same(PdfBoolean.True, sc.Operands[0]);
        Assert.Same(PdfBoolean.False, sc.Operands[1]);
        Assert.Same(PdfNull.Instance, sc.Operands[2]);
    }

    [Fact]
    public void RealOperandOfTwoMillionDigits_isParsedOnTheHeap_andRunReturns()
    {
        // TryParseOperandNumber used to size its padding buffer with a raw stackalloc keyed on the
        // numeric token's own length. PdfLexer.ReadNumeric puts no bound on that length (it is a
        // content-stream operand, not a value this reader's own resource caps ever see), so an
        // operand of about 1.5 million digits overflowed the stack outright, an uncatchable crash
        // this type's own contract (above) says a malformed or absurd construct must not cause. On
        // the heap Utf8Parser still parses this exactly: a two-million-digit literal is conformant,
        // if absurd, §7.3.3 syntax, so its value survives rather than falling back to a malformed
        // report. This must not run against the pre-fix binary: there it kills the whole test host.
        var digits = new string('1', 2_000_000);
        var content = "0." + digits + " w\n1 w\n";
        // About 2 MiB of content, far under the default MaxDecodedStreamBytes the Run helper leaves
        // in force, so a limit diagnostic cannot be what Assert.Empty below is looking at.
        Assert.True(content.Length < ReaderLimits.DefaultMaxDecodedBytes);

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        var wCalls = visitor.Operators.Where(o => o.Op == "w").ToList();
        Assert.Equal(2, wCalls.Count);
        var real = (PdfReal)wCalls[0].Operands[0];
        Assert.Equal(0.1111111111, real.Value, 9);
        Assert.Equal(1, ((PdfInteger)wCalls[1].Operands[0]).Value);
        Assert.Empty(reader.Diagnostics);
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

    [Fact]
    public void UnknownOperatorOutsideBX_alsoDropsItsOperands()
    {
        // §7.8.2: "operands shall not be left over when an operator finishes execution" applies to
        // an unrecognised keyword's own (no-op) execution too, not only to a recognised one
        // (#402 round 2). Before the fix, the operands preceding an unrecognised operator outside a
        // BX/EX section survived to be misread by whatever operator followed: '20' here was fed
        // into 'w' as its own operand instead of the '1' that belongs to it.
        const string content = "10 20 Zork\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Equal(["w"], visitor.Operators.Select(o => o.Op));
        var w = Assert.Single(visitor.Operators);
        Assert.Equal(1, ((PdfInteger)w.Operands[0]).Value);
        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnknownOperator);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
    }

    [Fact]
    public void BxEx_ownArityIsChecked_butTheSectionStillOpensAndCloses()
    {
        // BX and EX are both arity-0 (Table 33), but used to be dispatched before the generic
        // arity check ran at all, so leftover operands on either one reported nothing even though
        // _arity["BX"] and _arity["EX"] are both 0 (#402 round 3). The fix runs the same arity
        // check on BX/EX as every other operator, but unlike an ordinary mismatch (which drops the
        // whole call), a compatibility section still opens or closes regardless of what garbage
        // operands preceded BX/EX: Table 33 does not condition that on anything. Two pages, not
        // one: the sink's dedupe key is (code, object, page), so a malformed BX and a malformed EX
        // on the SAME page/object would collapse to one recorded diagnostic even though both
        // independently fire and independently clear their own operands (see
        // UnknownOperatorInsideBX for the same dedupe behaviour on a different code); splitting
        // them across two pages is what makes both individually observable in reader.Diagnostics.
        var doc = BuildPdf(
            1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
            new Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "1 2 3 BX\nEX\nQ\n"u8.ToArray()),
            new Obj(5,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents 6 0 R >>"),
            new Obj(6, "<< >>", "BX\n4 EX\nQ\n"u8.ToArray()));

        var reader = PdfReader.Open(doc, new PdfReaderOptions());
        var interpreter = new ContentInterpreter(reader);
        var visitor0 = new RecordingVisitor();
        var visitor1 = new RecordingVisitor();
        interpreter.Run(reader.GetPage(0), visitor0);
        interpreter.Run(reader.GetPage(1), visitor1);

        var mismatches = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed)
            .ToList();
        Assert.Equal(2, mismatches.Count);
        Assert.Contains(mismatches, d => d.Message.Contains("'BX'", StringComparison.Ordinal));
        Assert.Contains(mismatches, d => d.Message.Contains("'EX'", StringComparison.Ordinal));
        // Both sections still close on their own page: a form invoked with the SAME unknown
        // operator inside it would report UnknownOperator if EX had failed to close the section
        // (see UnknownOperatorInsideBX above for the positive case); here Q, well outside BX/EX by
        // the time it runs on each page, still reaches the visitor as an ordinary operator.
        Assert.Contains(visitor0.Operators, o => o.Op == "Q");
        Assert.Contains(visitor1.Operators, o => o.Op == "Q");
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
    public void HugeArrayOperand_capReportsBeforeMaterialisingIt_boundingAllocation()
    {
        // An array operand used to be fully materialised as a PdfArray, with one boxed PdfInteger
        // per element, before the 8192-element cap was ever consulted (:737-745 against :491-494 at
        // 5f5f84b): a 20,000,000-element '[1 1 1 ...] TJ', source text well within the 64 MiB
        // content budget, allocated 1,784 MiB and committed 977 MiB before reporting one 309.
        // CompositeOperandWithinCap decides the cap with the lexer alone (#402 round 3), so this
        // array is never materialised at all. Built unfiltered (BuildPageDocRaw), not through
        // FlateDecode: decompressing a ~38 MiB result into a growable buffer is itself a sizeable
        // allocation this reader's own filter pipeline pays regardless of this fix, and folding it
        // into the same measurement would bound the WRONG thing (measured: 204.7 MiB total with
        // FlateDecode in the mix, most of it decompression, none of it this fix's own concern).
        const int elementCount = 20_000_000;
        var unit = "1 "u8.ToArray();
        var arrayBody = new byte[unit.Length * elementCount];
        for (var i = 0; i < elementCount; i++)
            unit.CopyTo(arrayBody, i * unit.Length);
        var content = "["u8.ToArray().Concat(arrayBody).Concat("] TJ\n1 w\n"u8.ToArray()).ToArray();
        var doc = BuildPageDocRaw(content);

        // Measured per thread, not process-wide: the test host runs classes in parallel, and the
        // process-wide counter charged this delta with whatever the other classes allocated at the
        // same moment (1125.1 MiB and 373.1 MiB on the CI runner for a shape that costs 76.3 MiB
        // alone). Run interprets synchronously on the calling thread, so the thread-local counter
        // sees exactly this call's allocations.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var (reader, _, visitor) = Run(doc);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.Equal(["w"], visitor.Operators.Select(o => o.Op));
        // An allocation bound, not a wall-clock one (#400): generous by design, not tight. 96 MiB
        // is comfortably above the ~38 MiB source text this reader still reads and lexes, and far
        // below the 1,784 MiB the pre-fix path allocated for the identical shape.
        Assert.True(
            allocated < 96L * 1024 * 1024,
            $"expected under 96 MiB allocated for the capped array; measured "
            + $"{allocated / (1024.0 * 1024.0):F1} MiB.");
    }

    [Fact]
    public void HugeNestedArrayOperand_countsEveryDepth_boundingAllocation()
    {
        // The first version of CompositeOperandWithinCap counted depth-1 tokens only, so
        // '[[1 1 1 ...]] TJ' counted as ONE element, passed the cap, and was materialised in full:
        // the same 1,784 MiB shape as the flat array above, hidden behind a single pair of
        // brackets. Every token at every depth counts now.
        const int elementCount = 20_000_000;
        var unit = "1 "u8.ToArray();
        var arrayBody = new byte[unit.Length * elementCount];
        for (var i = 0; i < elementCount; i++)
            unit.CopyTo(arrayBody, i * unit.Length);
        var content = "[["u8.ToArray().Concat(arrayBody).Concat("]] TJ\n1 w\n"u8.ToArray()).ToArray();
        var doc = BuildPageDocRaw(content);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var (reader, _, visitor) = Run(doc);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Single(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.Equal(["w"], visitor.Operators.Select(o => o.Op));
        Assert.True(
            allocated < 96L * 1024 * 1024,
            $"expected under 96 MiB allocated for the capped nested array; measured "
            + $"{allocated / (1024.0 * 1024.0):F1} MiB.");
    }

    [Fact]
    public void HugeUnterminatedArrayOperand_isDroppedAsOverCap_notMaterialisedBeforeTheLexError()
    {
        // An unterminated composite used to return "within cap" from the pre-scan so that
        // ParseObject could re-derive the ContentStreamLexError, which it did, but only after
        // allocating every element up to the point of failure. Over the cap, the composite is now
        // dropped with one 309 and never parsed; the lexer is left at end of input, so no 300
        // follows and nothing else is reported.
        const int elementCount = 20_000_000;
        var unit = "1 "u8.ToArray();
        var arrayBody = new byte[unit.Length * elementCount];
        for (var i = 0; i < elementCount; i++)
            unit.CopyTo(arrayBody, i * unit.Length);
        var content = "1 w\n["u8.ToArray().Concat(arrayBody).ToArray();
        var doc = BuildPageDocRaw(content);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var (reader, _, visitor) = Run(doc);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Single(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.Equal(["w"], visitor.Operators.Select(o => o.Op));
        Assert.True(
            allocated < 96L * 1024 * 1024,
            $"expected under 96 MiB allocated for the unterminated array; measured "
            + $"{allocated / (1024.0 * 1024.0):F1} MiB.");
    }

    [Fact]
    public void SmallUnterminatedArrayOperand_stillReportsTheLexError_notTheCap()
    {
        // Within the cap, an unterminated composite keeps its original outcome: ParseObject
        // re-derives the failure and the stream ends with ContentStreamLexError, not 309.
        var (reader, _, visitor) = Run(BuildPageDoc("1 w\n[1 2 3\n"));

        Assert.Single(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.Equal(["w"], visitor.Operators.Select(o => o.Op));
    }

    [Fact]
    public void OverCapArrayOperand_withAMalformedTokenInside_reportsTheLexErrorAlongsideTheCap()
    {
        // 9000 elements is over MaxCompositeOperandElements (8192), and the unterminated string
        // right after them is its own lex failure (no closing ')' anywhere in the rest of the
        // buffer), not merely the count pass bailing out at the cap. Before this fix, the count
        // pass's own catch swallowed that failure silently: only 309 was reported, with nothing to
        // explain why interpretation also stopped dead right there, even though the identical
        // failure UNDER the cap (see the next test) already reported 300 through ParseObject's own
        // re-parse (#402 round 4).
        var content = "[" + string.Concat(Enumerable.Repeat("1 ", 9000)) + "(unterminated\n] TJ\n1 w\n";
        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        Assert.Empty(visitor.Operators);
    }

    [Fact]
    public void UnderCapArrayOperand_withAMalformedTokenInside_reportsOnlyTheLexError()
    {
        // Same malformed string, well under the cap: ParseObject's own re-parse already derives
        // this as ContentStreamLexError, with no ContentLimitExceeded at all, since the count pass
        // never came near the cap.
        const string content = "[1 1 (unterminated\n] TJ\n2 w\n";
        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.Empty(visitor.Operators);
    }

    [Fact]
    public void UnbalancedQ_isIgnoredWithADiagnostic()
    {
        var (reader, _, visitor) = Run(BuildPageDoc("Q\n1 w\n"));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.Contains(visitor.Operators, o => o.Op == "w"); // interpretation continued
    }

    // ── Over-cap 'q'/'BMC' pushes must not desync a later balanced pop (#402 round 2) ─────────────

    [Theory]
    [InlineData(65)]
    [InlineData(70)]
    public void BalancedQAndQ_pastTheGraphicsStateCap_reportsOnlyTheLimit_andStaysBalanced(int depth)
    {
        // Before the fix, the pushes past MaxGraphicsStateDepth (64) were dropped but their
        // matching 'Q's still popped anyway, so a balanced 'q'...'Q' sequence deeper than the cap
        // reported BOTH ContentLimitExceeded (for the dropped pushes) AND OperandStackMalformed
        // (for the "Q"s that found nothing real left on the stack once the real pushes were
        // exhausted), and desynchronised GraphicsState.Ctm from the actual nesting besides.
        var content = string.Concat(Enumerable.Repeat("q\n", depth))
            + "2 0 0 2 10 20 cm\n"
            + string.Concat(Enumerable.Repeat("Q\n", depth));

        var interpreter = RunAndKeepInterpreter(BuildPageDoc(content), out var reader);

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        // Every 'q' was balanced by a 'Q', so the CTM the 'cm' set inside is restored to identity,
        // whether or not this reader's own stack held every one of those saves.
        Assert.Equal(Matrix.Identity, interpreter.GraphicsState.Ctm);
    }

    [Fact]
    public void BalancedBmcAndEmc_pastTheMarkedContentCap_reportsOnlyTheLimit_andStaysBalanced()
    {
        const int depth = 65;
        var content = string.Concat(Enumerable.Repeat("/Tag BMC\n", depth))
            + string.Concat(Enumerable.Repeat("EMC\n", depth))
            + "1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    // ── A push dropped for one of this reader's own ceilings (not the depth cap above, which
    // already credits its own drops) still credits its matching pop, so 'EMC'/'Q' does not report
    // an unbalanced pop purely because this reader declined to push the state it was asked to
    // (#402 round 4) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BdcDroppedForAnOverCapPropertiesDictionary_creditsTheMatchingEmc()
    {
        // §14.6.2 puts no size bound on a property list. 5000 key/value pairs is 10,000 tokens,
        // over MaxCompositeOperandElements (8192), so BDC's own dictionary operand is dropped and
        // BDC itself never reaches the visitor; before this fix, the balancing EMC then reported an
        // unbalanced pop on top of the 309, even though the document itself is conformant.
        var props = string.Concat(Enumerable.Range(0, 5000).Select(i => $"/K{i} {i} "));
        var content = $"/Tag << {props}>> BDC\nEMC\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Single(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.DoesNotContain(visitor.Operators, o => o.Op == "BDC");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void QDroppedForTheOperandCountCap_creditsTheMatchingQ()
    {
        // 65 numeric operands ahead of 'q' overflow MaxOperandsPerOperator (64), so 'q' itself is
        // dropped for the SAME reason PushOperand already reported (no second diagnostic); the
        // matching 'Q' must still consume that credit silently rather than report an unbalanced pop.
        var content = string.Join(' ', Enumerable.Repeat("1", 65)) + " q\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Single(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.DoesNotContain(visitor.Operators, o => o.Op == "q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void QDroppedForTheOperandCountCap_creditsOnlyOneQ_aSecondQStillReportsUnbalanced()
    {
        // Only ONE 'q' was dropped, so only one credit exists: a second 'Q' past it finds nothing
        // pushed and no credit left, and reports the ordinary unbalanced-pop diagnostic. This test
        // guards over-crediting rather than pinning the round-4 fix itself (the two sibling tests
        // do that): a credit of 2 for the one dropped 'q' would silence both 'Q's, leaving
        // `unbalanced` empty and failing Assert.Single below.
        var content = string.Join(' ', Enumerable.Repeat("1", 65)) + " q\nQ\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentLimitExceeded);
        var unbalanced = reader.Diagnostics
            .Where(d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed)
            .ToList();
        Assert.Single(unbalanced);
        Assert.Contains("'Q'", unbalanced[0].Message, StringComparison.Ordinal);
        Assert.Contains(visitor.Operators, o => o.Op == "w");
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

    // ── An operand of the wrong type at the right arity reports 302 and drops the operator,
    // rather than NumberOperand silently substituting 0 or Do silently no-op'ing (#402 round 3) ───

    [Fact]
    public void Cm_withANonNumericOperand_reportsOnce_andLeavesTheCtmUntouched()
    {
        // Before this fix, NumberOperand substituted 0 for the string operand with no diagnostic,
        // so this delivered Ctm = [1 0 0 1 0 50] instead of leaving the identity CTM untouched.
        var interpreter = RunAndKeepInterpreter(
            BuildPageDoc("1 0 0 1 (x) 50 cm\n"), out var reader);

        Assert.Equal(Matrix.Identity, interpreter.GraphicsState.Ctm);
        var reports = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed)
            .ToList();
        Assert.Single(reports);
        Assert.Contains("'cm'", reports[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TfWithAStringSizeAndTdWithANameOperand_bothDropped_stateUntouched()
    {
        // Before this fix: Tf's non-numeric size operand ("(12)", a string) silently coerced to a
        // FontSize of 0 (already the default, so this alone would not have been visible), but Td's
        // non-numeric ty operand ("/N", a name) coerced to 0 while tx=10 was still applied,
        // delivering TextMatrix = [1 0 0 1 10 0] instead of leaving it at the identity. Both Tf and
        // Td are dropped entirely now; only one 302 is recorded, since the sink dedupes on (code,
        // object, page) and both reports share the same key.
        var interpreter = RunAndKeepInterpreter(
            BuildPageDoc(
                "BT\n/F1 (12) Tf\n10 /N Td\nET\n", "<< /Font << /F1 5 0 R >> >>",
                new Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")),
            out var reader);

        Assert.Null(interpreter.GraphicsState.Font);
        Assert.Equal(0, interpreter.GraphicsState.FontSize);
        Assert.Equal(Matrix.Identity, interpreter.TextState.TextMatrix);
        var reports = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed)
            .ToList();
        Assert.Single(reports);
    }

    [Fact]
    public void Do_withANonNameOperand_reportsOnce_andNeverReachesTheVisitor()
    {
        // Before this fix, a non-name operand to Do silently no-op'd: EmitAndClear had already
        // reported "Do" to the visitor before the (missing) type check would have run, and neither
        // 302 nor 306 was ever reported. Do is now type-checked BEFORE it is emitted, so a
        // malformed invocation like this never reaches the visitor at all.
        var content = "42 Do\n";
        var doc = BuildPageDoc(
            content, "<< /XObject << /F1 5 0 R >> >>",
            new Obj(5, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] >>", "1 w\n"u8.ToArray()));

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(visitor.Operators, o => o.Op == "Do");
        Assert.Empty(visitor.FormBegins);
        Assert.Contains(
            reader.Diagnostics,
            d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed
                && d.Message.Contains("'Do'", StringComparison.Ordinal));
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
    }

    // ── ' and " (Table 107 line-showing operators, #402 round 4) ───────────────────────────────────

    [Fact]
    public void Quote_movesToTheNextLine_andForwardsTheStringOperandUntouched()
    {
        // Table 107: "' ... shall have the same effect as the code T* string Tj". Before this fix,
        // ' fell through to the default (state-inert) case: the text matrices were left untouched
        // and the string operand still reached the visitor, since forwarding happens either way.
        const string content = "BT\n100 TL\n5 0 Td\n(a) '\nET\n";
        var (reader, interpreter, visitor) = Run(BuildPageDoc(content));

        // Td moves the line matrix to [1 0 0 1 5 0]; ' then moves by (0, -TL=-100):
        // Tlm_new = [1 0 0 1 0 -100] x [1 0 0 1 5 0] = [1 0 0 1 5 -100].
        Assert.Equal(new Matrix(1, 0, 0, 1, 5, -100), interpreter.TextState.TextLineMatrix);
        Assert.Equal(new Matrix(1, 0, 0, 1, 5, -100), interpreter.TextState.TextMatrix);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);

        var quote = visitor.Operators.Single(o => o.Op == "'");
        var str = Assert.Single(quote.Operands);
        Assert.Equal("a", Encoding.ASCII.GetString(((PdfLiteralString)str).Bytes.Span));
    }

    [Fact]
    public void DoubleQuote_setsWordAndCharSpacing_thenMovesToTheNextLine()
    {
        // Table 107: "\" ... shall have the same effect as this code: aw Tw ac Tc string '": aw and
        // ac land in the text state before the T*-equivalent move.
        const string content = "BT\n50 TL\n7 8 (x) \"\nET\n";
        var (reader, interpreter, visitor) = Run(BuildPageDoc(content));

        Assert.Equal(7, interpreter.GraphicsState.WordSpacing);
        Assert.Equal(8, interpreter.GraphicsState.CharSpacing);
        // TextLineMatrix starts at identity (no Td/Tm ran); " moves by (0, -TL=-50):
        // Tlm_new = [1 0 0 1 0 -50] x [1 0 0 1 0 0] = [1 0 0 1 0 -50].
        Assert.Equal(new Matrix(1, 0, 0, 1, 0, -50), interpreter.TextState.TextLineMatrix);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);

        var quote = visitor.Operators.Single(o => o.Op == "\"");
        Assert.Equal(3, quote.Operands.Count);
    }

    [Fact]
    public void DoubleQuote_withNonNumericSpacingOperands_reportsOnce_andIsDropped()
    {
        const string content = "BT\n(a) (b) (x) \"\nET\n";
        var (reader, interpreter, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(visitor.Operators, o => o.Op == "\"");
        Assert.Equal(0, interpreter.GraphicsState.WordSpacing);
        Assert.Equal(0, interpreter.GraphicsState.CharSpacing);
        Assert.Equal(Matrix.Identity, interpreter.TextState.TextLineMatrix);
        var reports = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed)
            .ToList();
        Assert.Single(reports);
    }

    [Fact]
    public void Quote_withANonStringOperand_reportsOnce_andIsDropped()
    {
        const string content = "BT\n5 '\nET\n";
        var (reader, interpreter, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(visitor.Operators, o => o.Op == "'");
        Assert.Equal(Matrix.Identity, interpreter.TextState.TextLineMatrix);
        var reports = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed)
            .ToList();
        Assert.Single(reports);
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

    // ── gs/cs/CS/sh read their own operand for a resource lookup, so a wrong type reports 302 the
    // same way Do's own does, rather than the lookup silently no-op'ing (#402 round 4) ────────────

    [Theory]
    [InlineData("12 gs\n")]
    [InlineData("5 sh\n")]
    [InlineData("(x) cs\n")]
    [InlineData("7 CS\n")]
    public void ResourceLookupOperator_withANonNameOperand_reportsOnce_andNeverReachesTheVisitor(
        string content)
    {
        // Before this fix: HandleExtGState/ValidateColorSpaceResource/ValidateNamedResource each
        // read _operands[0] as a name unchecked, so a numeric or string operand there silently
        // no-op'd the resource lookup with no diagnostic at all, the same gap Do had before round 3.
        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Empty(visitor.Operators);
        var reports = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed)
            .ToList();
        Assert.Single(reports);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
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
    public void SelfReferencingForm_withMaxDepthOne_stillReportsCycle_notDepthExceeded()
    {
        // With MaxFormXObjectDepth this tight, the outer 'Do' (page -> Form11, depth 0 -> 1)
        // succeeds, so Form11's own self-'Do' hits _formDepth == the cap on the very same
        // invocation that also closes the cycle. Before the fix, the depth check ran BEFORE the
        // cycle check, so this reported FormXObjectDepthExceeded (technically true, but strictly
        // less informative than the actual reason: the recursion is not merely deep, it never
        // terminates at all) (#402 round 2).
        var doc = BuildPageDoc(
            "/F1 Do\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(
                11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] "
                + "/Resources << /XObject << /F1 11 0 R >> >> >>",
                "/F1 Do"u8.ToArray()));

        var (reader, _, _) = Run(doc, new PdfReaderOptions { MaxFormXObjectDepth = 1 });

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FormXObjectCycle);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FormXObjectDepthExceeded);
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

    [Fact]
    public void FormInvokedInsideBX_startsItsOwnCompatibilitySection_notInheritedFromTheInvoker()
    {
        // Table 33 scopes a BX/EX compatibility section to ONE content stream; a form is its own
        // content stream. Before this fix, HandleDo saved and restored _bxDepth but never RESET it
        // for the form's own scope, so a form invoked from inside 'BX ... Do ... EX' inherited the
        // invoker's _bxDepth > 0, making the form's own unknown operator look like it was still
        // inside the INVOKER's compatibility section and silently swallowing it, even though the
        // same form invoked as a bare 'Do' correctly reports it (#402 round 3).
        var doc = BuildPageDoc(
            "BX\n/F1 Do\nEX\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", "10 20 Zork\n1 w\n"u8.ToArray()));

        var (reader, _, visitor) = Run(doc);

        var reports = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.UnknownOperator).ToList();
        Assert.Single(reports);
        Assert.Contains("'Zork'", reports[0].Message, StringComparison.Ordinal);
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    // ── §8.10.1 b): the form's own /Matrix is concatenated into the CTM (#402 round 2) ───────────

    [Fact]
    public void Do_concatenatesTheFormsOwnMatrixIntoTheCtm_forOperatorsInsideTheForm()
    {
        var doc = BuildPageDoc(
            "/F1 Do\n1 w\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(
                11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Matrix [2 0 0 2 10 10] >>",
                "0 0 1 1 re"u8.ToArray()));

        Matrix? ctmInsideForm = null;
        var reader = PdfReader.Open(doc);
        var interpreter = new ContentInterpreter(reader);
        var probe = new CtmProbeVisitor(() => ctmInsideForm ??= interpreter.GraphicsState.Ctm);
        interpreter.Run(reader.GetPage(0), probe);

        // §8.3.4: CTM_new = M x CTM_old; the invoker's own CTM at the point of Do is identity, so
        // the composed value equals the form's own /Matrix exactly.
        Assert.Equal(new Matrix(2, 0, 0, 2, 10, 10), ctmInsideForm);
        // The invoker's own CTM is untouched once Do returns.
        Assert.Equal(Matrix.Identity, interpreter.GraphicsState.Ctm);
    }

    private sealed class CtmProbeVisitor(Action onFirstOperatorInsideForm) : IContentVisitor
    {
        private bool _insideForm;

        public void OnOperator(string operatorName, IReadOnlyList<PdfObject> operands, int offset)
        {
            if (_insideForm)
                onFirstOperatorInsideForm();
        }

        public void OnInlineImage(PdfDictionary dictionary, ReadOnlyMemory<byte> data, int offset) { }

        public void OnFormBegin(
            PdfDictionary formDictionary, Matrix formMatrix, PdfRectangle? boundingBox, int objectNumber,
            int offset) =>
            _insideForm = true;

        public void OnFormEnd(int objectNumber) => _insideForm = false;
    }

    // ── HandleDo reports a resource that resolves but is not a usable XObject (#402 round 2) ─────

    [Fact]
    public void Do_namingAnEntryThatIsNotAnIndirectReference_reportsResourceMissing()
    {
        var (reader, _, visitor) = Run(BuildPageDoc(
            "/F1 Do\n1 w\n", "<< /XObject << /F1 /NotAReference >> >>"));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
        Assert.Contains(visitor.Operators, o => o.Op == "w"); // interpretation continued past Do
    }

    [Fact]
    public void Do_namingAReferenceThatDoesNotResolveToAStream_reportsResourceMissing()
    {
        var (reader, _, visitor) = Run(BuildPageDoc(
            "/F1 Do\n1 w\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Form >>"))); // no stream body: not a stream object

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void Do_namingAStreamWithNoSubtype_reportsResourceMissing()
    {
        var (reader, _, visitor) = Run(BuildPageDoc(
            "/F1 Do\n1 w\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject >>", []))); // /Subtype absent

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void Do_namingAStreamWithASubtypeThatIsNeitherFormNorImage_reportsResourceMissing()
    {
        var (reader, _, visitor) = Run(BuildPageDoc(
            "/F1 Do\n1 w\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Foo >>", [])));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void Do_namingAnImageXObject_reportsNoResourceMissing_andNoRecursion()
    {
        var (reader, _, visitor) = Run(BuildPageDoc(
            "/F1 Do\n1 w\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray >>", [0])));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
        Assert.Contains(visitor.Operators, o => o.Op == "Do");
        Assert.Empty(visitor.FormBegins);
        Assert.Contains(visitor.Operators, o => o.Op == "w");
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
    public void Do_onAFormThatOpensItsOwnTextObject_doesNotLeakTextMatricesIntoTheInvoker()
    {
        // Before the fix, _textState was never saved/restored around a form invocation (the class
        // doc reasoned Do could never appear inside a text object, so there was nothing to
        // disturb; that reasoning missed that the FORM's own content can open an entirely
        // independent text object regardless of the invoker's own state). A form whose own content
        // opens, moves, and closes its own text object silently overwrote the invoker's own
        // TextMatrix once Do returned, with no diagnostic at all (#402 round 2).
        var doc = BuildPageDoc(
            "/F1 Do\n1 w\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(
                11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>",
                "BT 999 888 Td ET"u8.ToArray()));

        var interpreter = RunAndKeepInterpreter(doc, out _);

        Assert.Equal(Matrix.Identity, interpreter.TextState.TextMatrix);
        Assert.Equal(Matrix.Identity, interpreter.TextState.TextLineMatrix);
    }

    [Fact]
    public void Do_insideATextObject_reportsOperandStackMalformedOnce_andStillRecurses()
    {
        // §8.2 Figure 9 admits no XObjects-category (Table 50) operator inside a text object. This does
        // not itself stop the recursion (the form is still resolvable and drawn); it is purely a
        // producer-side diagnostic (#402 round 2).
        var doc = BuildPageDoc(
            "BT\n/F1 Do\nET\n1 w\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", []));

        var (reader, _, visitor) = Run(doc);

        var reports = reader.Diagnostics.Where(d =>
            d.Code == PdfReaderDiagnosticCode.OperandStackMalformed
            && d.Message.Contains("text object", StringComparison.Ordinal)).ToList();
        Assert.Single(reports);
        Assert.Single(visitor.FormBegins);
        Assert.Contains(visitor.Operators, o => o.Op == "w"); // interpretation continued
    }

    [Fact]
    public void Do_insideATextObject_judgesTheFormsOwnDoAgainstTheFormsOwnTextObjectState()
    {
        // The form's content starts outside any text object whatever the invoker was doing, so a
        // 'Do' in a form that opens no BT of its own is not a violation even when the invoking
        // 'Do' sat inside one. Before the HandleDo bracket saved the flag, the form's 'Do' was
        // reported against the form's object number as well (#402 round 2).
        var doc = BuildPageDoc(
            "BT\n/F1 Do\nET\n", "<< /XObject << /F1 11 0 R >> >>",
            new Obj(
                11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /F2 12 0 R >> >> >>",
                "/F2 Do\n"u8.ToArray()),
            new Obj(12, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", []));

        var (reader, _, visitor) = Run(doc);

        var reports = reader.Diagnostics.Where(d =>
            d.Code == PdfReaderDiagnosticCode.OperandStackMalformed
            && d.Message.Contains("text object", StringComparison.Ordinal)).ToList();
        var report = Assert.Single(reports);
        Assert.NotEqual(11, report.ObjectNumber);
        Assert.Equal(2, visitor.FormBegins.Count);
    }

    [Fact]
    public void Do_onAFormWithAnUnbalancedBT_doesNotLeaveTheInvokerInsideATextObject()
    {
        // The flag is restored when the form returns, so a form whose content opens BT and never
        // closes it (a §9.4.1 violation of the form's own) cannot make the invoker's next 'Do'
        // look like it sits inside a text object.
        var doc = BuildPageDoc(
            "/F1 Do\n/F2 Do\n1 w\n", "<< /XObject << /F1 11 0 R /F2 12 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", "BT\n"u8.ToArray()),
            new Obj(12, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", []));

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d =>
            d.Code == PdfReaderDiagnosticCode.OperandStackMalformed
            && d.Message.Contains("text object", StringComparison.Ordinal));
        Assert.Equal(2, visitor.FormBegins.Count);
        Assert.Contains(visitor.Operators, o => o.Op == "w");
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

    // ── The per-Run content budget is charged per element as chunks arrive (#402 round 2) ────────

    [Fact]
    public void ContentsArray_referencingTheSameOversizedStreamTenTimes_decodesOnlyFourElements()
    {
        // Before the fix, BuildPageContentBuffer decoded and held EVERY /Contents array element in
        // memory before Concatenate ever got a chance to apply the budget: peak heap scaled with
        // element count x MaxDecodedStreamBytes rather than with the budget itself. Ten references
        // to the same ~20 MiB-decoding stream reproduces that (one 20 MiB Flate stream referenced
        // 128 times from a 103 KB file measured 4662 MiB peak managed heap while interpreting
        // exactly the same operators a four-element array would); charging the budget as each
        // chunk arrives means only the first four references are ever decoded at all. Three fit
        // comfortably inside the 64 MiB budget, and the fourth is what pushes the running total
        // over it, so ContentStreamsDecoded pins that count directly rather than asserting on
        // wall-clock time or process memory.
        var unit = "0 0 1 1 re\n"u8.ToArray();
        var repeatsPerChunk = (20 * 1024 * 1024) / unit.Length;
        var chunkBody = new byte[unit.Length * repeatsPerChunk];
        for (var i = 0; i < repeatsPerChunk; i++)
            unit.CopyTo(chunkBody, i * unit.Length);
        var encoded = Flate(chunkBody);

        var tenRefsDoc = BuildPdf(
            1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents [4 0 R 4 0 R 4 0 R 4 0 R 4 0 R 4 0 R 4 0 R 4 0 R "
                + "4 0 R 4 0 R] >>"),
            new Obj(4, "<< /Filter /FlateDecode >>", encoded));

        var (reader, interpreter, visitor) = Run(tenRefsDoc);

        Assert.Equal(4, interpreter.ContentStreamsDecoded);
        var tooLarge = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.ContentStreamTooLarge).ToList();
        Assert.Single(tooLarge);

        var fourRefsDoc = BuildPdf(
            1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents [4 0 R 4 0 R 4 0 R 4 0 R] >>"),
            new Obj(4, "<< /Filter /FlateDecode >>", encoded));
        var (_, _, fourVisitor) = Run(fourRefsDoc);

        Assert.Equal(fourVisitor.Operators.Count, visitor.Operators.Count);
    }

    [Fact]
    public void FormDecodingToEightMiB_drawn64Times_decodesExactlyEightTimes()
    {
        // A2 (#402 round 2): HandleDo checked _contentBytesRemaining AFTER decoding a form's
        // content, so a decode whose result was about to be discarded (budget already spent) still
        // paid the full allocation and filter cost every single invocation. 64 MiB / 8 MiB = 8: the
        // budget check hoisted above the decode means invocations past the 8th skip decoding
        // entirely rather than decoding (and counting) a 9th time only to discard it, the way an
        // inexactly-sized form still would (a sliver of budget left over after 8 whole decodes
        // still reads as "budget available" to the entry check, so a 9th decode still runs and only
        // gets truncated afterward).
        var unit = "0 0 1 1 re\n"u8.ToArray();
        const int formSize = 8 * 1024 * 1024;
        var repeats = formSize / unit.Length;
        var formBody = new byte[formSize];
        for (var i = 0; i < repeats; i++)
            unit.CopyTo(formBody, i * unit.Length);
        Array.Fill(formBody, (byte)'\n', repeats * unit.Length, formSize - repeats * unit.Length);

        var pageContent = string.Concat(Enumerable.Repeat("/F1 Do\n", 64));
        var doc = BuildPageDoc(
            pageContent, "<< /XObject << /F1 11 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", formBody));

        var (_, interpreter, _) = Run(doc);

        // 9, not 8: ContentStreamsDecoded also counts the page's own /Contents decode
        // (BuildPageContentBuffer.AddElement increments it too, per A1), which runs once before
        // any 'Do' and spends a small slice of the same budget on the page's own "'/F1 Do' x 64"
        // bytes; the 8 form decodes below are what this test pins.
        Assert.Equal(9, interpreter.ContentStreamsDecoded);
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

    // ── NOTE 2's "skip any further white-space" for ASCIIHexDecode/ASCII85Decode (#402 round 2) ──

    [Fact]
    public void A85WithTwoSpacesAfterId_andExplicitL_skipsBothSeparatorBytes()
    {
        // §8.9.7 NOTE 2: once the single required separator is consumed, a filter of
        // ASCIIHexDecode or ASCII85Decode skips any FURTHER white-space too, before /L's own bytes
        // are counted. Before this fix, at most one whitespace byte was ever consumed regardless of
        // filter, so the second space here was misread as the payload's own first byte and /L=4
        // came out 3 bytes short of the real data.
        byte[] data = "ABCD"u8.ToArray();
        var content = $"BI /F /A85 /L {data.Length} ID  "
            + Encoding.ASCII.GetString(data) + " EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void A85WithTwoSpacesAfterId_andNoL_stillSkipsBothSeparatorBytes()
    {
        // Same as above but falling all the way to the EI scan (tier c) instead of an explicit /L:
        // before this fix, the extra space landed inside the scanned data too, so the recovered
        // bytes were " ABCD" (5, with the stray leading space) rather than "ABCD" (4).
        byte[] data = "ABCD"u8.ToArray();
        var content = "BI /F /A85 ID  " + Encoding.ASCII.GetString(data) + " EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void A85WithCrLfThenTwoMoreSpaces_skipsAllOfThem()
    {
        // The CR-LF-as-one-EOL-marker separator (§7.2.3) is consumed first, then the extra
        // whitespace loop below still applies on top of it for an ASCII85Decode filter.
        byte[] data = "ABCD"u8.ToArray();
        var content = $"BI /F /A85 /L {data.Length} ID\r\n  "
            + Encoding.ASCII.GetString(data) + " EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void AHxWithTwoSpacesAfterId_andExplicitL_skipsBothSeparatorBytes()
    {
        byte[] data = "ABCD"u8.ToArray();
        var content = $"BI /F /AHx /L {data.Length} ID  "
            + Encoding.ASCII.GetString(data) + " EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void RawImageWithTwoSpacesAfterId_keepsTheSecondSpaceAsData()
    {
        // Control: a filter NOT in {ASCIIHexDecode, ASCII85Decode} (here, no filter at all) still
        // consumes exactly one separator byte, per the normative sentence's own "Unless ...". The
        // second space is the payload's own first sample byte, not something to skip.
        var content = "BI /W 5 /H 1 /BPC 8 /CS /G ID  ABCD EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(" ABCD"u8.ToArray()));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Fact]
    public void FilterArrayWithA85Second_doesNotSkipExtraWhitespace_flateOwnsThePosition()
    {
        // §8.9.7 NOTE 2's skip recipe applies only to the filter that reads the raw bytes right
        // after ID: the FIRST element of a /Filter array (decode order, §7.4.1's own EXAMPLE 2),
        // never a later one. '/F [/Fl /A85]' names FlateDecode first, so FlateDecode owns the raw
        // bytes; A85's presence at position 1 must not trigger the extra-whitespace skip meant for
        // whichever filter sits at position 0 (#402 round 3: an earlier version skipped whenever
        // AHx/A85 appeared ANYWHERE in the array, eating the payload's own leading 0x20 byte here
        // and sliding the whole 8-byte window one byte late, delivering "62 63 64 65 66 67 68 20"
        // instead of the correct "20 62 63 64 65 66 67 68").
        byte[] payload = [0x20, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68];
        var content = "BI /F [/Fl /A85] /L 8 ID "u8.ToArray()
            .Concat(payload)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(payload));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void FilterArrayWithANonNameElementZero_doesNotSkipExtraWhitespace()
    {
        // '/F [null /A85]': CollectFilterNames drops the non-name element rather than keeping its
        // place in the array, so a naive filterNames[0] read would see /A85 there and wrongly skip
        // extra whitespace as though A85 itself sat at position 0. The raw array's own position 0
        // is null, neither AHx nor A85, so no extra whitespace should be skipped here at all
        // (#402 round 4). The payload's own leading byte is a space (0x20): if the bug were still
        // present, the extra-whitespace-skip loop would eat it as though it followed a
        // single-white-space-then-skip-more rule that does not apply to this filter shape.
        byte[] payload = [0x20, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68];
        var content = "BI /F [null /A85] /L 8 ID "u8.ToArray()
            .Concat(payload)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(payload));
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
        // The harder false candidate: " EI " followed by bytes outside ISO 32000-2 §7.2.3 Tables 1
        // and 2's whitespace and delimiter sets, which the lexer accepts as one Keyword token
        // without throwing. Only the probe's operator check (the keyword is not in Annex A Table
        // A.1) rejects this one; the terminating EI follows and is accepted.
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

    // ── The bounded resync probe (#402 round 3 redesign): a per-Run byte budget shared by every
    // 'EI' candidate, not a per-candidate window, bounds ScanForEi's total cost regardless of
    // content size or candidate count ────────────────────────────────────────────────────────────

    [Fact]
    public void ManyFalseEiCandidates_atOneMebibyte_exhaustsTheProbeBudgetExactlyOnce()
    {
        // Reproduces the pre-round-3 cost blowup this shape drove into the two-window probe: N
        // repeats of " EI (" (a false candidate immediately followed by the start of a literal
        // string that never closes anywhere in the rest of the buffer) after a DCT-filtered
        // image's own data, then the terminating ' EI'. Measured against the round-2 probe: 1 MiB
        // decoded content, 16,608 ms; 4 MiB, 71,320 ms; the 64 MiB content cap extrapolates to
        // about 19 minutes, with no diagnostic to explain the cost. Every candidate here is close
        // enough to the buffer's own true end that the round-3 probe's window is UNCLIPPED for the
        // first several: each of those pays a full scan to the true end trying to close the never-
        // closing string (an outright Reject, not a budget artefact) before the 16 MiB run budget
        // itself runs out partway through one candidate's own window, which is what turns that one
        // candidate's own outcome into Exhausted (accepted unverified) instead. One MiB of content
        // is what makes that transition land inside this one test run: about 16 unclipped full-
        // buffer rejects (~1 MiB each) exhaust the 16 MiB budget, so the total cost this shape can
        // ever impose is bounded by the budget, not by how many further false candidates follow.
        const int decodedContentBytes = 1024 * 1024;
        var falseCandidate = " EI ("u8.ToArray();
        var prefix = "BI /F /DCT ID "u8.ToArray().Concat<byte>([0xFF, 0xD8, 0xFF]).ToArray();
        var suffix = " EI\nQ\n"u8.ToArray();
        var noiseLength = decodedContentBytes - prefix.Length - suffix.Length;
        var repeats = noiseLength / falseCandidate.Length;
        var noise = new byte[falseCandidate.Length * repeats];
        for (var i = 0; i < repeats; i++)
            falseCandidate.CopyTo(noise, i * falseCandidate.Length);

        var content = prefix.Concat(noise).Concat(suffix).ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, interpreter, visitor) = Run(doc);

        const long maxProbeBytesPerRun = 16L * 1024 * 1024; // mirrors ContentInterpreter's own private const
        Assert.Equal(maxProbeBytesPerRun, interpreter.ProbeBytesConsumed);

        // Exactly the budget report and the outer lexer's own report on the unterminated literal
        // string the accepted-unverified candidate leaves behind (#402 round 3): the accepted
        // candidate's own trailing '(' never closes anywhere in the rest of the buffer (every later
        // '(' in the noise just nests deeper), so InterpretStream's own main loop hits
        // InvalidDataException once it reaches true end of buffer still inside that string.
        Assert.Equal(2, reader.Diagnostics.Count);
        Assert.Contains(
            reader.Diagnostics,
            d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed
                && d.Message.Contains("accepted without verification", StringComparison.Ordinal));
        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        Assert.Single(visitor.InlineImages); // the (false) accepted-unverified candidate delimits it
    }

    [Fact]
    public void DctImage_withNoFalseCandidates_atFourMebibytes_probesAtMostTheTrailingOperator()
    {
        // The other half of the round-3 bound: a clean image (no false 'EI' candidates at all)
        // must cost the probe almost nothing, however large the image itself is, since ScanForEi
        // finds the terminating 'EI' on its very first byte-level match and LooksLikeResyncPoint probes
        // forward from there exactly once.
        const int imageBytes = 4 * 1024 * 1024;
        var data = new byte[imageBytes];
        // 'E'/'I' bytes deliberately excluded so no coincidental candidate exists in the noise.
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(0x01 + (i % 0x40));

        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, interpreter, visitor) = Run(doc);

        // <= 2: the bytes of 'Q' the single ProbeOnce call from just past the terminating 'EI' consumes.
        Assert.True(
            interpreter.ProbeBytesConsumed <= 2,
            $"expected the probe to spend at most 2 bytes on a candidate-free image; spent "
            + $"{interpreter.ProbeBytesConsumed}.");
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Theory]
    [InlineData(4093)] // token exactly 4096 bytes: already correct under the old two-window probe
    [InlineData(4094)]
    [InlineData(5000)]
    [InlineData(40_000)]
    public void RealEi_followedByALiteralOfAnyLength_isAlwaysAccepted_noExtendedWindowLeft(int litLen)
    {
        // Round-3 HIGH 2: the round-2 probe's extended (4096-byte) window rejected the terminating
        // 'EI' when the legitimate token right after it was longer than that window, losing the
        // image and the rest of the stream (litLen 4094 and up reported InlineImageMalformed and
        // delivered nothing). The round-3 probe has no per-candidate window at all, only the shared
        // per-Run budget (far larger than any of these), so every length here is accepted the same
        // way.
        var longLiteral = new string('Y', litLen);
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
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    public static IEnumerable<object[]> NeutralStraddlingShapes()
    {
        yield return ["[1 2 3"]; // unterminated array
        yield return ["<</K /V"]; // unterminated dictionary
        yield return ["(closed) (closed) (closed)"]; // three well-formed, closed literal strings
    }

    [Theory]
    [MemberData(nameof(NeutralStraddlingShapes))]
    public void FalseEiCandidate_followedByANeutralShapeThenTheRealEi_isRejected_dataNotTruncated(
        string shape)
    {
        // Round-3 MEDIUM 1: the round-2 fix closed the straddling-token hole only for token kinds
        // that make PdfLexer throw (an unterminated string). A neutral token kind ProbeOnce treats
        // as "keep lexing" (an array, a dictionary, or a run of well-formed strings) gave the
        // identical wrong "accept the false candidate" outcome, silently truncating the data to a
        // handful of bytes with no diagnostic. The round-3 probe requires a POSITIVE reason to
        // accept, not merely the absence of a reject, so probing forward from the false candidate
        // here reaches the trailing 'EI' keyword itself (Reject, "still inside data that continues
        // to a later EI") before any Table A.1 operator, correctly rejecting the false one and
        // letting the outer scan continue to the terminating EI.
        var falseCandidate = " EI "u8.ToArray();
        var filler = new byte[300];
        Array.Fill(filler, (byte)'z');
        byte[] jpegNoise = [0x01, 0x02, 0xFF, 0xD8, 0xFF];
        var data = jpegNoise
            .Concat(falseCandidate)
            .Concat(Encoding.ASCII.GetBytes(shape))
            .Concat(filler)
            .ToArray();

        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.InRange(img.Data.Length, 315, 335);
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StreamEndingRightAfterEi_stillDelimitsTheImage(bool trailingWhitespace)
    {
        // A candidate probed at the buffer's own true end (not merely a budget-clipped window) is
        // a legitimate resync point on its own: AtEnd on an unclipped window accepts.
        byte[] data = [0xFF, 0xD8, 0xFF];
        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat(trailingWhitespace ? " EI \n"u8.ToArray() : " EI"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void ScanForEi_dataEndingInCrLfBeforeEi_stripsBothBytes()
    {
        // §8.9.7 excludes "the white-space delimiting those operators" from the image data; §7.2.3
        // makes a CARRIAGE RETURN immediately followed by a LINE FEED ONE EOL marker. Before this
        // fix, ScanForEi stripped only the LF, leaving a trailing 0D byte in the delivered data
        // (#402 round 3; the ID side already consumed a CR LF pair as one separator).
        byte[] data = [0xFF, 0xD8, 0xFF];
        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat("\r\nEI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
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
        // The round-2 probe's own first, 128-byte window ran off mid-string here (the literal is
        // 200 bytes, longer than that window) and needed a second, larger window to accept it. The
        // round-3 probe has no fixed window at all, only the shared per-Run budget (#402 round 3),
        // so a 200-byte literal that closes properly is neutral and simply keeps the probe lexing
        // until it reaches 'Tj', a known operator: accepted the same way, just without a retry.
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

    // ── A token that never closes at all is rejected regardless of how far it runs, since the
    // shared per-Run budget only turns "ran out of budget" into an accept, never "ran off a fixed
    // per-candidate window" (#402 round 3; see ProbeOnce's windowClipped distinction) ────────────

    [Theory]
    [InlineData(100)]
    [InlineData(110)]
    [InlineData(200)]
    [InlineData(5000)]
    public void FalseEiCandidate_insideAnUnterminatedLiteralString_isRejected_regardlessOfLength(int n)
    {
        // A false ' EI' candidate immediately followed by an unterminated literal string that
        // never closes ANYWHERE in the rest of the buffer, then N filler bytes, then the
        // terminating ' EI\nQ\n'. The remaining buffer here (a few KB at most) is always far
        // smaller than the 16 MiB per-Run probe budget, so the probe's window is never clipped by
        // that budget for any of these: the string never closes at all, so it is rejected outright
        // at every N tested, the same way a round-1 regression (accepting a token that merely ran
        // past a FIXED window) never had a chance to reappear once that fixed window itself was
        // removed.
        var falseCandidate = " EI (never closed"u8.ToArray();
        var filler = new byte[n];
        Array.Fill(filler, (byte)'z');
        byte[] jpegNoise = [0x01, 0x02, 0xFF, 0xD8, 0xFF];
        var data = jpegNoise.Concat(falseCandidate).Concat(filler).ToArray();

        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void FalseEiCandidate_insideAnUnterminatedHexString_isRejected()
    {
        // The hex-string counterpart of the literal-string case above: an unclosed '<' run that
        // never closes anywhere in the rest of the buffer.
        var falseCandidate = " EI <414243"u8.ToArray(); // '<' opens a hex string that never closes
        var filler = new byte[200];
        Array.Fill(filler, (byte)'0');
        byte[] jpegNoise = [0x01, 0x02, 0xFF, 0xD8, 0xFF];
        var data = jpegNoise.Concat(falseCandidate).Concat(filler).ToArray();

        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(data)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void RealEi_followedByATerminated4000ByteLiteral_isAccepted()
    {
        // The true terminating 'EI' must still be accepted even when what follows it is a long but
        // well-formed token: rejecting the resync point that terminates the image just because
        // legitimate content after it happens to be long would be exactly as wrong as accepting one
        // that never closes at all. See RealEi_followedByALiteralOfAnyLength_isAlwaysAccepted_noExtendedWindowLeft
        // for the round-3 boundary cases the old two-window probe got wrong (litLen >= 4094).
        var longLiteral = new string('Y', 4000);
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

    // ── The terminating 'EI' followed by a token that runs off the buffer's own TRUE end (not
    // merely a probe window) is weaker evidence than a malformed byte found strictly inside an
    // unclipped window, so it is accepted as a fallback rather than rejected outright the way that
    // stronger case still is (#402 round 4; see ProbeOutcome.WeakReject and ScanForEi) ─────────────

    [Theory]
    [InlineData("(abc")] // unterminated literal string, runs to the buffer's true end
    [InlineData("<AABB")] // unterminated hex string, same
    [InlineData("(abc\n1 w\nQ\n")] // the same unterminated literal, with trailing bytes that never
                                   // close it either, so it still runs to the buffer's true end
    [InlineData("<AABB\n1 w\nQ\n")] // the hex-string counterpart of the row above
    public void TerminatingEi_followedByATokenThatNeverCloses_isStillAccepted(string tail)
    {
        // Before this fix, an exception thrown at the buffer's own TRUE end (not an artificial
        // probe-window clip) was treated exactly like a malformed byte found strictly inside the
        // window: an outright Reject. That misjudged the ONE 'EI' this content has as a
        // false candidate, losing the image and reporting a false InlineImageMalformed, when the
        // 'EI' was correctly delimited all along, and the unterminated token after it belongs to
        // the OUTER lexer's own ContentStreamLexError instead.
        byte[] jpeg = [0xFF, 0xD8, 0xFF];
        var content = "BI /F /DCT ID "u8.ToArray()
            .Concat(jpeg)
            .Concat(" EI "u8.ToArray())
            .Concat(Encoding.ASCII.GetBytes(tail))
            .ToArray();
        var doc = BuildPageDocRaw(content);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(jpeg));
        Assert.Empty(visitor.Operators);
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
    public void CrAsSingleSeparator_withDataStartingWithLf_landsOnEiWithNoRetry()
    {
        // §8.9.7's own single-white-space-byte reading, taken as this reader's primary one (#402
        // round 4): the separator is a lone CR, and the image's own declared length (/L 4) starts
        // right after it, at the LF the producer meant as the image's own first data byte. Folding
        // CR LF together here (the old primary reading) would have eaten that LF as part of the
        // separator instead, dropping it from the delivered data.
        byte[] data = [0x0A, 0x41, 0x42, 0x43];
        var raw = "BI /W 4 /H 1 /BPC 8 /CS /G /L 4 ID\r"u8.ToArray()
            .Concat(data)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(raw);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void CrLfSeparator_whoseOneByteReadingMissesEi_recoversViaTheRetry()
    {
        // The twin of the repro just above: here the producer wrote a two-byte CR LF separator
        // (§7.2.3's own fold), so the one-byte-first primary reading (CR alone) lands one
        // byte short of 'EI' and must retry with both bytes consumed, the mirror image of the retry
        // 'CrAsSingleSeparator_withDataStartingWithLf_landsOnEiWithNoRetry' does not need.
        byte[] data = "ABCD"u8.ToArray();
        var raw = "BI /W 4 /H 1 /BPC 8 /CS /G /L 4 ID\r\n"u8.ToArray()
            .Concat(data)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(raw);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void CrLfSeparator_whosePayloadEndsInWhiteSpace_isReadTheStrictWayWithNoRetry()
    {
        // Pins the §8.9.7 one-byte reading for this ambiguous file, not a defect. The producer
        // wrote a two-byte CR LF separator, and the payload's own true last byte is itself white
        // space (LF), so the strict one-byte reading, which leaves the payload's true first byte
        // (the LF from the CR-LF pair) at the front of the data instead of consuming it as part of
        // the separator, still lands on 'EI': SkipToEi skips leading white space before 'EI', and
        // that displaced trailing byte gets skipped the same way. The CR-LF retry above this test
        // never runs, so the visitor receives the data shifted one byte with no diagnostic; §8.9.7
        // gives the reader no way to tell this apart from a producer that meant a lone CR instead.
        byte[] delivered = [0x0A, 0x41, 0x42, 0x43];
        byte[] payload = [0x41, 0x42, 0x43, 0x0A];
        var raw = "BI /W 4 /H 1 /BPC 8 /CS /G /L 4 ID\r\n"u8.ToArray()
            .Concat(payload)
            .Concat("EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(raw);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(delivered));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void CrLfSeparator_whoseLengthIsWrongUnderBothReadings_recoversThroughTheScanWithoutALeadingLf()
    {
        // /L 2 is wrong for a 4-byte payload, so neither the one-byte reading nor the CR LF retry
        // lands on 'EI' and the image falls through to the EI scan. That scan has no length to
        // verify a reading against, so it takes tier c's own reading, the CR LF fold: the recovered
        // data must be the payload alone, not the payload with the separator's LF prepended.
        byte[] data = "ABCD"u8.ToArray();
        var raw = "BI /W 4 /H 1 /BPC 8 /CS /G /L 2 ID\r\n"u8.ToArray()
            .Concat(data)
            .Concat(" EI\nQ\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(raw);

        var (reader, _, visitor) = Run(doc);

        var diag = Assert.Single(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        Assert.Contains("did not land on an 'EI' operator", diag.Message);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void CrAsSingleSeparator_withDataStartingWithLf_recoversWithoutAWarning()
    {
        // §8.9.7 mandates exactly ONE white-space byte after 'ID'; here that byte is a lone CR, and
        // the image's own first byte happens to be LF. §7.2.3's CR-LF-as-one-EOL-marker fold has no
        // role here (#402 round 4): this reader's own primary reading takes only the CR as the
        // separator, so the 5-byte window (LF,'A','B','C','D') this repro needs is exactly what the
        // primary reading produces on the first try now, landing directly on 'EI' with no retry at
        // all. Before round 4's inversion, the primary reading folded CR LF together, shifting the
        // window one byte late and needing a retry to recover; before round 2, that retry existed
        // but InlineImageMalformed was reported unconditionally ahead of it regardless of outcome,
        // so a conforming file recovered cleanly still carried a warning about it.
        var raw = "BI /W 5 /H 1 /BPC 8 /CS /G ID\r"u8.ToArray()
            .Concat("\nABCDEI\nQ\n1 w\n"u8.ToArray())
            .ToArray();
        var doc = BuildPageDocRaw(raw);

        var (reader, _, visitor) = Run(doc);

        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual("\nABCD"u8.ToArray()));
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

    [Fact]
    public void L_asARealNumber_reportsMalformed_andRecoversViaTheEiScan()
    {
        // Table 91 types /L as an integer; a PdfReal there (dropped silently before this fix) is
        // reported the same way an invalid /W, /H, or /BPC already is (#402 round 2). The message
        // says "present but" rather than "missing, or" (#402 round 3: this branch is reachable only
        // once /L IS present, so a "missing" message here was never accurate).
        var content = "BI /F /AHx /L 4.0 ID ABCD EI\nQ\n1 w\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(
            reader.Diagnostics,
            d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed
                && d.Message.Contains("present but its value is not an integer", StringComparison.Ordinal));
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual("ABCD"u8.ToArray()));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
        Assert.Contains(visitor.Operators, o => o.Op == "w");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(9)]
    public void InvalidBpc_notInTheFixedSet_takesTheEiScanPath(int bpc)
    {
        // Table 87 restricts /BPC's own value to 1, 2, 4, 8, or 16, not merely "a positive
        // integer"; a value like 3 was accepted before this fix (#402 round 2).
        byte[] data = "ABCD"u8.ToArray();
        var content = $"BI /W 2 /H 2 /BPC {bpc} /CS /G ID "
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
    public void WidthHeightBpc_allIntMaxValue_recoversViaTheEiScan_withoutOverflowing()
    {
        // int.MaxValue is rejected by the /BPC set-membership check before it ever reaches
        // TryComputeUnfilteredLength's own (long) width * bpc * components multiplication, closing
        // the theoretical overflow that check guards against (#402 round 2): this must recover
        // through the EI scan rather than throwing or hanging.
        byte[] data = "ABCD"u8.ToArray();
        var content = $"BI /W {int.MaxValue} /H {int.MaxValue} /BPC {int.MaxValue} /CS /G ID "
            + Encoding.ASCII.GetString(data) + " EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        var img = Assert.Single(visitor.InlineImages);
        Assert.True(img.Data.AsSpan().SequenceEqual(data));
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
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

    // §8.9.7 excludes JBIG2Decode, JPXDecode, and Crypt from inline-image use; the disallowed
    // filter still delimits the image (§8.9.7 gives this reader everything it needs to find the
    // 'EI'), so interpretation continues past it, but the callback is skipped for that one image
    // (#402 round 4: pinning all three filter names, not JPXDecode alone, and pinning that the
    // callback itself, not merely the diagnostic, is skipped).

    [Fact]
    public void Jbig2DecodeInlineImage_reportsMalformed_andTheStreamContinuesAfterIt()
    {
        var content = "BI /F /JBIG2Decode /L 3 ID abc EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Single(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        Assert.Empty(visitor.InlineImages);
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    [Fact]
    public void CryptInlineImage_reportsMalformed_andTheStreamContinuesAfterIt()
    {
        var content = "BI /F /Crypt /L 3 ID abc EI\nQ\n";

        var (reader, _, visitor) = Run(BuildPageDoc(content));

        Assert.Single(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
        Assert.Empty(visitor.InlineImages);
        Assert.Contains(visitor.Operators, o => o.Op == "Q");
    }

    // ── 307's own (code, object, page) dedupe is per CONTENT STREAM, not per page: a page invoking
    // two forms, each carrying its own malformed inline image, reports two 307s, one against each
    // form's own object number, since the sink never sees the same key twice (#402 round 4) ──────

    [Fact]
    public void InlineImageMalformed_inTwoDifferentForms_reportsTwice()
    {
        var formBody = "BI /F /JPXDecode /L 3 ID abc EI\nQ\n"u8.ToArray();
        var content = "/A Do\n/B Do\n";
        var doc = BuildPageDoc(
            content, "<< /XObject << /A 11 0 R /B 12 0 R >> >>",
            new Obj(11, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", formBody),
            new Obj(12, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>", formBody));

        var (reader, _, visitor) = Run(doc);

        var reports = reader.Diagnostics
            .Where(d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed)
            .ToList();
        Assert.Equal(2, reports.Count);
        Assert.Empty(visitor.InlineImages);
        Assert.Equal(2, visitor.Operators.Count(o => o.Op == "Q"));
    }

    [Fact]
    public void InlineImageMalformed_onThePagesOwnContentAlone_reportsOnce()
    {
        var content = "BI /F /JPXDecode /L 3 ID abc EI\nQ\n";

        var (reader, _, _) = Run(BuildPageDoc(content));

        Assert.Single(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
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

    [Fact]
    public void LexerFailureInsideOneStream_endsTheWholeConcatenatedBuffer_laterElementsIncluded()
    {
        // The 300 doc's "resumes with the next stream in the array" clause holds only for an
        // element that fails to RESOLVE or DECODE (NonStreamContentsElement_isSkippedWithLexError
        // above): every element that DOES decode is concatenated into one buffer before
        // interpretation of any of it begins (Table 31: "the division between streams may occur
        // only at the boundaries between lexical tokens"), so a lexer or parser failure inside that
        // buffer ends interpretation for the rest of the page's content, later array elements
        // included, not merely the one stream the failure fell in (#402 round 3). Object 4 decodes
        // to a token '1 0 ]' the object parser cannot make sense of (a lone ']' with no opening
        // '['); object 5's own 'J'/'j' never run.
        var doc = BuildPdf(
            1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << >> /Contents [4 0 R 5 0 R] >>"),
            new Obj(4, "<< >>", "1 w\n1 0 ]\n"u8.ToArray()),
            new Obj(5, "<< >>", "2 J\n3 j\n"u8.ToArray()));

        var (reader, _, visitor) = Run(doc);

        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        Assert.Equal(["w"], visitor.Operators.Select(o => o.Op));
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

    private static readonly byte[] FuzzContent = Encoding.ASCII.GetBytes(
        "q\n2 0 0 2 10 20 cm\nBT\n/F1 12 Tf\n(Hi) Tj\n[(A) -10 (B)] TJ\nET\n"
        + "/G1 gs\n/X1 Do\nBI /W 2 /H 2 /BPC 8 /CS /G ID \x01\x02\x03\x04 EI\nQ\n");

    private static byte[] BuildFuzzDoc(byte[] content) => BuildPdf(
        1,
        new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new Obj(3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 6 0 R >> /XObject << /X1 7 0 R >> "
            + "/ExtGState << /G1 8 0 R >> >> /Contents 4 0 R >>"),
        new Obj(4, "<< >>", content),
        new Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        new Obj(
            7, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Matrix [1 0 0 1 0 0] >>",
            "1 w\n"u8.ToArray()),
        new Obj(8, "<< /Type /ExtGState /Font [6 0 R 10] >>"));

    private static readonly byte[] FuzzSeed = BuildFuzzDoc(FuzzContent);

    private readonly record struct MutationOp(int Kind, int Position, byte Value, int Length);

    private static readonly Gen<MutationOp> MutationOpGen =
        Gen.Select(
            Gen.Int[0, 5], Gen.Int[0, int.MaxValue], Gen.Byte, Gen.Int[1, 32],
            (kind, position, value, length) => new MutationOp(kind, position, value, length));

    // Mutates the whole file's own bytes: exercises reconstruction, object resolution, and
    // structural malformation, but a mutation landing in the xref, trailer, or object headers
    // (most of the file, by byte count) usually keeps the interpreter from ever being reached at
    // all: see ContentBodyFuzzInputGen below for the generator that exists to reach it reliably
    // (#402 round 3).
    private static readonly Gen<byte[]> FuzzInputGen =
        MutationOpGen.Array[1, 8].Select(ops => Mutate(FuzzSeed, ops));

    // Mutates only the page's own content-stream BODY, then rebuilds a structurally valid document
    // around the result: every byte this generator can touch is one ContentInterpreter itself
    // walks, so PdfReader.Open and GetPage keep succeeding on nearly every sample regardless of
    // what the mutations did to operator syntax, giving Run deep, repeated coverage of the
    // interpreter's own malformed-content recovery paths rather than mostly exercising xref repair
    // (#402 round 3; ContentBodyFuzzGenerator_isRobust_andReachesRunOnTheMajorityOfSamples pins
    // this generator's own reach rate).
    private static readonly Gen<byte[]> ContentBodyFuzzInputGen =
        MutationOpGen.Array[1, 8].Select(ops => BuildFuzzDoc(Mutate(FuzzContent, ops)));

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
        // A block-bodied lambda, not `bytes => AssertInterpreterIsRobust(bytes)`: CsCheck.Sample has
        // both an Action<T> overload (fails only on a thrown exception) and a Func<T, bool> one
        // (fails whenever the delegate returns false). AssertInterpreterIsRobust now returns
        // whether Run was reached (#402 round 3, for the majority-of-samples test below), and an
        // expression-bodied lambda binds to the Func<T, bool> overload by exact delegate-type match,
        // silently reinterpreting "this sample never reached Run" (true for ~95% of this generator's
        // own samples) as a FAILING property case instead of the acceptable outcome it is. A
        // block-bodied lambda has no expression value to match Func<T, bool> against, so it can only
        // bind to Action<T>, restoring the original semantics.
        => FuzzInputGen.Sample(bytes => { AssertInterpreterIsRobust(bytes); }, iter: FuzzBudget.Iterations);

    [Fact]
    public void ContentBodyFuzzGenerator_isRobust_andReachesRunOnTheMajorityOfSamples()
    {
        // One Sample() call, not two: it both runs the same robustness assertions
        // Fuzz_run_neverThrowsOutsideTheDeclaredVocabulary_andAlwaysTerminates pins for the
        // file-wide generator AND counts how many samples reach ContentInterpreter.Run at all. A
        // separate Fact duplicating the robustness-only pass would double this generator's own
        // CsCheck cost (every core, per the class doc's own #398/#399 contention lesson) for no
        // extra coverage.
        //
        // The file-wide generator's own mutations mostly land in the xref, trailer, or object
        // headers (most of the file, by byte count), so most samples never resolve a page at all:
        // measured at 20,000 iterations, 990 of 19,628 samples (5.0%) reached Run: the interpreter
        // itself is exercised on only a sliver of that generator's own budget. The content-body
        // generator exists to fix that: every byte it can touch is inside the one content stream
        // ContentInterpreter itself walks, so the surrounding file structure stays intact and Run
        // is reached on nearly every sample instead (#402 round 3). Pinned by COUNT, not time
        // (#400): a majority is the bar, generous against the near-100% this generator reaches in
        // practice. threads: 1: this call does not need parallelism of its own to finish well
        // within a test run, and a second CsCheck.Sample call in this same class independently
        // trying to claim every core is needless oversubscription alongside
        // Fuzz_run_neverThrowsOutsideTheDeclaredVocabulary_andAlwaysTerminates's own (the class
        // doc's own #398/#399 note is why this file treats that as worth avoiding, not adding to).
        var reached = 0L;
        var total = 0L;
        ContentBodyFuzzInputGen.Sample(
            bytes =>
            {
                total++;
                if (AssertInterpreterIsRobust(bytes))
                    reached++;
            },
            iter: FuzzBudget.Iterations, threads: 1);

        Assert.True(total > 0);
        Assert.True(
            reached * 2 >= total,
            $"the content-body fuzz generator reached Run on {reached}/{total} samples; expected a "
            + "majority.");
    }

    /// <summary>Runs the same robustness assertions <see cref="Fuzz_run_neverThrowsOutsideTheDeclaredVocabulary_andAlwaysTerminates"/>
    /// pins, and reports whether this sample reached <c>ContentInterpreter.Run</c> at all, so a
    /// caller can separately measure how often each generator gets that far (#402 round 3).</summary>
    private static bool AssertInterpreterIsRobust(byte[] bytes)
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

        var reachedRun = page is not null;
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
        return reachedRun;
    }

    private static class FuzzBudget
    {
        private const long DefaultIterations = 3_000;

        /// <summary>
        /// Iterations per fuzz <see cref="Fact"/>/<see cref="Theory"/> case. Overridable via
        /// <c>VELLUMPDF_FUZZ_ITER</c> so the nightly workflow can run a much larger budget without
        /// the PR-gating default paying for it. A copy of <c>ParserFuzzTests.FuzzBudget</c> (#402
        /// round 4: restored the doc a prior copy of this type dropped), kept as two separate
        /// private nested types rather than one shared internal one, since the two test classes'
        /// own fuzz targets (the lexer/parser/reader pipeline there, this interpreter here) are
        /// unrelated enough that sharing state between them is not worth the coupling.
        /// </summary>
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
