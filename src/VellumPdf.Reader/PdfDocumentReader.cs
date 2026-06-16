// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Reader;

/// <summary>
/// A parsed PDF document opened via <see cref="PdfReader.Open(byte[])"/>.
/// Provides lazy object resolution, catalog access, and signature navigation.
/// </summary>
public sealed class PdfDocumentReader : IDisposable
{
    private readonly Dictionary<int, int> _xref;
    private readonly Dictionary<int, PdfObject> _cache = new();
    private IReadOnlyList<PdfSignature>? _signatures;

    internal ReadOnlyMemory<byte> Bytes { get; }
    internal PdfDictionary Trailer { get; }

    /// <summary>The byte offset recorded in the last startxref (the xref table offset for the newest revision).</summary>
    internal int StartXrefOffset { get; }

    /// <summary>Total length of the PDF byte buffer. Phase 3 uses this to compute append offsets.</summary>
    internal int TotalLength => Bytes.Length;

    /// <summary>The document catalog dictionary (/Root).</summary>
    public PdfDictionary Catalog { get; }

    /// <summary>All digital signatures found in the document's AcroForm, in field-tree order.</summary>
    public IReadOnlyList<PdfSignature> Signatures => _signatures ??= CollectSignatures();

    internal PdfDocumentReader(
        ReadOnlyMemory<byte> bytes,
        Dictionary<int, int> xref,
        PdfDictionary trailer,
        int startXrefOffset)
    {
        Bytes = bytes;
        _xref = xref;
        Trailer = trailer;
        StartXrefOffset = startXrefOffset;

        // Resolve the catalog immediately; a missing /Root is a fatal error.
        if (!trailer.TryGet(PdfName.Root, out var rootObj) || rootObj is null)
            throw new InvalidDataException("Malformed PDF: trailer is missing /Root.");

        var rootResolved = ResolveValue(rootObj);
        if (rootResolved is not PdfDictionary catalog)
            throw new InvalidDataException("Malformed PDF: /Root does not resolve to a dictionary.");

        Catalog = catalog;
    }

    /// <summary>Resolves an indirect reference by object number, using the xref table and object cache.</summary>
    internal PdfObject? Resolve(int objectNumber)
    {
        if (_cache.TryGetValue(objectNumber, out var cached))
            return cached;

        if (!_xref.TryGetValue(objectNumber, out var offset))
            return null;

        var parser = new PdfObjectParser(Bytes, offset);
        var result = parser.ParseIndirectObject();

        PdfObject value = result.IsStream
            ? result.Stream!.Dictionary
            : result.Value ?? PdfNull.Instance;

        _cache[objectNumber] = value;
        return value;
    }

    /// <summary>Resolves an indirect reference.</summary>
    internal PdfObject? Resolve(PdfIndirectReference r) => Resolve(r.ObjectNumber);

    /// <summary>
    /// If <paramref name="obj"/> is a <see cref="PdfIndirectReference"/>, resolves and returns
    /// the target object. Otherwise returns <paramref name="obj"/> unchanged.
    /// </summary>
    internal PdfObject? ResolveValue(PdfObject obj) =>
        obj is PdfIndirectReference r ? Resolve(r) : obj;

    /// <inheritdoc />
    public void Dispose() { }

    // ── Incremental update / append ──────────────────────────────────────────

    /// <summary>
    /// The current object count from the base trailer's /Size field.
    /// Callers assign new object numbers starting here (Size, Size+1, …).
    /// </summary>
    internal int Size
    {
        get
        {
            if (Trailer.TryGet(PdfName.Size, out var sizeObj) && sizeObj is PdfInteger sizeInt)
                return (int)sizeInt.Value;
            return 0;
        }
    }

    /// <summary>
    /// Appends a new revision to this document and returns the full updated byte array.
    ///
    /// Each element of <paramref name="objects"/> is written as an indirect object at
    /// absolute file offsets, then a classic incremental xref section and trailer are
    /// appended with <c>/Prev</c> pointing at the base document's <c>startxref</c>.
    ///
    /// The returned bytes can be passed back to <see cref="PdfReader.Open(byte[])"/> — the newer
    /// revision's object overrides take effect automatically via the /Prev chain.
    /// </summary>
    /// <param name="objects">
    /// Pairs of (objectNumber, value) to write. Object numbers must be unique; existing
    /// object numbers override the base revision; new numbers must be &gt;= <see cref="Size"/>.
    /// May be supplied in any order.
    /// </param>
    internal byte[] AppendRevision(IReadOnlyList<(int ObjectNumber, PdfObject Value)> objects)
    {
        if (objects.Count == 0)
            throw new ArgumentException("At least one object is required.", nameof(objects));

        var ms = new MemoryStream(Bytes.Length + 4096);
        ms.Write(Bytes.Span);

        var writer = new PdfWriter(ms, Bytes.Length);

        // Write each indirect object, recording its absolute file offset.
        var written = new List<(int ObjectNumber, long ByteOffset)>(objects.Count);
        foreach (var (objNum, value) in objects)
        {
            var offset = writer.Position;
            new PdfIndirectObject(objNum, value).WriteTo(writer);
            writer.WriteByte((byte)'\n');
            written.Add((objNum, offset));
        }

        // Extract the base /Root reference.
        PdfIndirectReference catalogRef;
        if (Trailer.TryGet(PdfName.Root, out var rootRaw) && rootRaw is PdfIndirectReference rootRef)
            catalogRef = rootRef;
        else
            throw new InvalidDataException("Base trailer does not contain a valid /Root indirect reference.");

        // Extract the base /ID array if present.
        PdfArray? documentId = null;
        if (Trailer.TryGet(PdfName.ID, out var idRaw) && idRaw is PdfArray idArr)
            documentId = idArr;

        // Write the incremental xref + trailer.
        IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
            writer,
            written,
            Size,
            catalogRef,
            StartXrefOffset,
            documentId);

        writer.Flush();
        return ms.ToArray();
    }

    // ── Signature navigation ─────────────────────────────────────────────────

    private List<PdfSignature> CollectSignatures()
    {
        var sigs = new List<PdfSignature>();

        var acroFormRaw = Catalog.Get(new PdfName("AcroForm"));
        if (acroFormRaw is null) return sigs;

        var acroFormObj = ResolveValue(acroFormRaw);
        if (acroFormObj is not PdfDictionary acroForm)
            return sigs;

        var fieldsRaw = acroForm.Get(new PdfName("Fields"));
        if (fieldsRaw is null)
            return sigs;

        var fields = ResolveValue(fieldsRaw);
        if (fields is not PdfArray fieldsArray)
            return sigs;

        for (var i = 0; i < fieldsArray.Count; i++)
            CollectFieldSignatures(fieldsArray[i], sigs);

        return sigs;
    }

    private void CollectFieldSignatures(PdfObject fieldObj, List<PdfSignature> sigs)
    {
        var resolved = ResolveValue(fieldObj);
        if (resolved is not PdfDictionary field)
            return;

        // Check /FT — may be inherited, but for top-level sig fields it is present.
        var ftObj = field.Get(new PdfName("FT"));
        if (ftObj is PdfName ft && ft.Value == "Sig")
        {
            // Get the signature value dict from /V
            var vObj = field.Get(new PdfName("V"));
            if (vObj is not null)
            {
                var sigDict = ResolveValue(vObj) as PdfDictionary;
                if (sigDict is not null)
                {
                    var sig = ExtractSignature(sigDict);
                    if (sig is not null)
                        sigs.Add(sig);
                }
            }
            return;
        }

        // If /FT is not /Sig, check for /Kids (field tree node).
        var kidsObj = field.Get(PdfName.Kids);
        if (kidsObj is not null)
        {
            var kids = ResolveValue(kidsObj);
            if (kids is PdfArray kidsArray)
            {
                for (var i = 0; i < kidsArray.Count; i++)
                    CollectFieldSignatures(kidsArray[i], sigs);
            }
        }
    }

    private static PdfSignature? ExtractSignature(PdfDictionary sigDict)
    {
        // /SubFilter
        PdfName? subFilter = null;
        var sfObj = sigDict.Get(new PdfName("SubFilter"));
        if (sfObj is PdfName sfName)
            subFilter = sfName;

        // /ByteRange [off0 len0 off1 len1]
        var brObj = sigDict.Get(new PdfName("ByteRange"));
        int[] byteRange = [];
        if (brObj is PdfArray brArr)
        {
            byteRange = new int[brArr.Count];
            for (var i = 0; i < brArr.Count; i++)
            {
                if (brArr[i] is PdfInteger pi)
                    byteRange[i] = (int)pi.Value;
            }
        }

        // /Contents — the raw DER bytes stored as a hex string
        var contentsObj = sigDict.Get(PdfName.Contents);
        ReadOnlyMemory<byte> contents = ReadOnlyMemory<byte>.Empty;
        if (contentsObj is PdfHexString hexStr)
            contents = hexStr.Bytes;

        // /M — signing time as a string
        string? signingTime = null;
        var mObj = sigDict.Get(new PdfName("M"));
        if (mObj is PdfLiteralString ls)
            signingTime = Encoding.Latin1.GetString(ls.Bytes.Span);
        else if (mObj is PdfHexString mHex)
            signingTime = Encoding.Latin1.GetString(mHex.Bytes.Span);

        if (contents.IsEmpty && byteRange.Length == 0)
            return null;

        return new PdfSignature(subFilter, byteRange, contents, signingTime);
    }
}
