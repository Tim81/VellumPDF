// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// ISO 19005-2 §6.2.11.4.1 (Glyph presence). Every glyph referenced for rendering shall be present
/// in the embedded font program. For a composite font using Identity encoding, the bytes shown by a
/// text operator are glyph indices directly, so any glyph index at or beyond the embedded program's
/// glyph count refers to a glyph that does not exist.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.11.4.1 and ISO 32000-1:2008, 9.4.3 / 9.7.4. Clean-room:
/// derived from the specification text. The embedded program's glyph count comes from
/// <see cref="SfntGlyphCount"/> (the maxp table); the glyph indices used are read from the page
/// content streams. Cross-validated against veraPDF (a Type0/Identity-H font shown a glyph index
/// beyond its TrueType program's glyph count fails clause 6.2.11.4.1-2).
/// <para>
/// This slice covers composite (Type 0) fonts with Identity-H/Identity-V encoding and an embedded
/// CIDFontType2 (TrueType) program — the common embedded-subset path. Simple-font glyph presence
/// (which needs encoding + cmap resolution), CFF programs, and glyphs drawn from within form
/// XObjects, patterns, Type 3 procedures, or annotation appearances are deferred.
/// </para>
/// </remarks>
internal sealed class GlyphPresenceRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.11.4.1-glyph-present";

    public string Clause => "ISO 19005-2:2011, 6.2.11.4.1";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _fontFile2 = new("FontFile2");

    public void Evaluate(PreflightContext context)
    {
        var reported = new HashSet<int>(); // font-program object numbers already flagged

        foreach (var page in context.EnumeratePages())
        {
            // Map each Identity-encoded Type0/CIDFontType2 font resource to its program glyph count.
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fonts)
                continue;

            var identityFonts = new Dictionary<string, IdentityFont>(StringComparer.Ordinal);
            foreach (var entry in fonts.Entries)
                if (TryGetIdentityFont(context, entry.Value) is { } font)
                    identityFonts[entry.Key.Value] = font;

            if (identityFonts.Count == 0)
                continue;

            var content = ContentStreamUsage.GetPageContent(context, page);
            if (content is null)
                continue;

            ScanContent(context, content, identityFonts, reported);
        }
    }

    private readonly record struct IdentityFont(int ProgramObject, int NumGlyphs);

    // Returns the embedded TrueType glyph count for a Type0 font with Identity-H/V encoding and a
    // CIDFontType2 descendant, or null when the font is not of that form.
    private static IdentityFont? TryGetIdentityFont(PreflightContext context, PdfObject? fontRef)
    {
        if (context.Resolve(fontRef) is not PdfDictionary font)
            return null;
        if (context.Resolve(font.Get(PdfName.Subtype)) is not PdfName { Value: "Type0" })
            return null;
        if (context.Resolve(font.Get(_encoding)) is not PdfName { Value: "Identity-H" or "Identity-V" })
            return null;
        if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants || descendants.Count == 0)
            return null;
        if (context.Resolve(descendants[0]) is not PdfDictionary cidFont
            || context.Resolve(cidFont.Get(PdfName.Subtype)) is not PdfName { Value: "CIDFontType2" })
            return null;
        if (context.Resolve(cidFont.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return null;
        if (descriptor.Get(_fontFile2) is not PdfIndirectReference fontFileRef
            || context.ResolveStream(fontFileRef) is not { } program
            || context.DecodeStream(program) is not { } programBytes)
            return null;
        if (SfntGlyphCount.TryGetNumGlyphs(programBytes) is not { } numGlyphs)
            return null;
        return new IdentityFont(fontFileRef.ObjectNumber, numGlyphs);
    }

    // Walks the content stream, tracking the current font, and for an Identity-encoded font treats
    // the bytes of each shown string as 2-byte big-endian glyph indices.
    private void ScanContent(
        PreflightContext context, byte[] content, Dictionary<string, IdentityFont> identityFonts, HashSet<int> reported)
    {
        IdentityFont? current = null;
        try
        {
            var lexer = new PdfLexer(content);
            string? lastName = null;
            var pending = new List<byte[]>();

            while (!lexer.AtEnd)
            {
                var token = lexer.NextToken();
                if (token.Kind == TokenKind.EndOfInput)
                    break;

                switch (token.Kind)
                {
                    case TokenKind.Name:
                        // The font name is the first operand of `name size Tf`, so it is not the token
                        // immediately before Tf; keep it until the next operator consumes it.
                        lastName = DecodeName(token.Raw.Span);
                        break;
                    case TokenKind.LiteralString:
                    case TokenKind.HexString:
                        pending.Add(DecodeString(token.Raw.Span, token.Kind == TokenKind.HexString));
                        break;
                    case TokenKind.Keyword:
                        var op = Encoding.Latin1.GetString(token.Raw.Span);
                        if (op == "Tf")
                            current = lastName is not null && identityFonts.TryGetValue(lastName, out var f) ? f : null;
                        else if (op is "Tj" or "TJ" or "'" or "\"")
                            ConsumeGlyphs(context, current, pending, reported);
                        pending.Clear();
                        lastName = null; // operands are consumed by the operator
                        break;
                    default:
                        // Numbers and array delimiters are operands — keep the pending name and strings.
                        break;
                }
            }
        }
        catch
        {
            // Malformed content — stop scanning this page; rules degrade rather than abort.
        }
    }

    private void ConsumeGlyphs(
        PreflightContext context, IdentityFont? current, List<byte[]> strings, HashSet<int> reported)
    {
        if (current is not { } font || reported.Contains(font.ProgramObject))
            return;
        foreach (var bytes in strings)
        {
            for (var i = 0; i + 1 < bytes.Length; i += 2)
            {
                var gid = (bytes[i] << 8) | bytes[i + 1];
                if (gid >= font.NumGlyphs && reported.Add(font.ProgramObject))
                {
                    context.Report(RuleId, Clause, PreflightSeverity.Error,
                        $"A glyph (index {gid}) drawn with a composite font is not present in the embedded "
                        + $"font program, which defines {font.NumGlyphs} glyphs.");
                    return;
                }
            }
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length; i++) // skip leading '/'
        {
            if (raw[i] == (byte)'#' && i + 2 < raw.Length && Hex(raw[i + 1]) >= 0 && Hex(raw[i + 2]) >= 0)
            {
                sb.Append((char)((Hex(raw[i + 1]) << 4) | Hex(raw[i + 2])));
                i += 2;
            }
            else
            {
                sb.Append((char)raw[i]);
            }
        }
        return sb.ToString();
    }

    // Decodes a content-stream string token (with its delimiters) to its raw bytes.
    private static byte[] DecodeString(ReadOnlySpan<byte> raw, bool hex)
    {
        var bytes = new List<byte>(raw.Length);
        if (hex)
        {
            var hi = -1;
            for (var i = 1; i < raw.Length && raw[i] != (byte)'>'; i++)
            {
                var v = Hex(raw[i]);
                if (v < 0)
                    continue;
                if (hi < 0)
                {
                    hi = v;
                }
                else
                {
                    bytes.Add((byte)((hi << 4) | v));
                    hi = -1;
                }
            }
            if (hi >= 0)
                bytes.Add((byte)(hi << 4)); // odd final digit is padded with a trailing zero
            return bytes.ToArray();
        }

        for (var i = 1; i < raw.Length && raw[i] != (byte)')'; i++)
        {
            if (raw[i] == (byte)'\\' && i + 1 < raw.Length)
            {
                i++;
                bytes.Add(raw[i] switch
                {
                    (byte)'n' => (byte)'\n',
                    (byte)'r' => (byte)'\r',
                    (byte)'t' => (byte)'\t',
                    (byte)'b' => (byte)'\b',
                    (byte)'f' => (byte)'\f',
                    _ => raw[i],
                });
            }
            else
            {
                bytes.Add(raw[i]);
            }
        }
        return bytes.ToArray();
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
