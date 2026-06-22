// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules;

/// <summary>
/// Records one text-show event in a page content stream: the font resource name selected by the
/// most recent <c>Tf</c> operator, the text rendering mode (<c>Tr</c>, default 0), and the raw
/// string bytes that were shown. <c>FontResourceName</c> may be <see langword="null"/> when no
/// preceding <c>Tf</c> was seen; <c>RenderingMode</c> is –1 when the rendering mode could not be
/// determined (parse gap — consumers should treat this as "unknown" / not a confirmed visible draw).
/// </summary>
internal sealed record TextShow(string? FontResourceName, int RenderingMode, byte[] Bytes);

/// <summary>
/// The result of scanning a page's content streams for graphics-state usage. All properties
/// mirror the seven operands tracked before Batch A5a, extended with <see cref="TextShows"/>.
/// </summary>
internal sealed class ContentUsage
{
    internal ContentUsage(
        HashSet<string> appliedExtGStates,
        bool usesDeviceColour,
        HashSet<string> drawnXObjects,
        HashSet<string> renderingIntents,
        HashSet<string> selectedColorSpaces,
        HashSet<string> usedFonts,
        HashSet<string> paintedShadings,
        List<TextShow> textShows)
    {
        AppliedExtGStates = appliedExtGStates;
        UsesDeviceColour = usesDeviceColour;
        DrawnXObjects = drawnXObjects;
        RenderingIntents = renderingIntents;
        SelectedColorSpaces = selectedColorSpaces;
        UsedFonts = usedFonts;
        PaintedShadings = paintedShadings;
        TextShows = textShows;
    }

    /// <summary>The ExtGState resource names actually applied by a <c>gs</c> operator.</summary>
    public HashSet<string> AppliedExtGStates { get; }

    /// <summary>True when the page paints with device-dependent colour.</summary>
    public bool UsesDeviceColour { get; }

    /// <summary>The XObject resource names actually painted by a <c>Do</c> operator.</summary>
    public HashSet<string> DrawnXObjects { get; }

    /// <summary>Rendering intents set by the <c>ri</c> operator.</summary>
    public HashSet<string> RenderingIntents { get; }

    /// <summary>Colour space names set by <c>cs</c>/<c>CS</c> operators.</summary>
    public HashSet<string> SelectedColorSpaces { get; }

    /// <summary>Font resource names selected by <c>Tf</c> operators (usage-scoped).</summary>
    public HashSet<string> UsedFonts { get; }

    /// <summary>Shading resource names painted by <c>sh</c> operators.</summary>
    public HashSet<string> PaintedShadings { get; }

    /// <summary>
    /// One entry per text-show operator (<c>Tj</c>, <c>TJ</c>, <c>'</c>, <c>"</c>) in document order.
    /// Each entry records the current font resource name, the current text rendering mode (–1 = unknown),
    /// and the raw bytes of the string shown. For <c>TJ</c>, one <see cref="TextShow"/> is emitted per
    /// string element in the array; number elements (spacing adjustments) are skipped.
    /// </summary>
    public IReadOnlyList<TextShow> TextShows { get; }
}

/// <summary>
/// A minimal content-stream operator scan. It reports which graphics-state (<c>/ExtGState</c>)
/// resources a page actually applies (via the <c>gs</c> operator), which XObjects it actually paints
/// (via the <c>Do</c> operator), which font resource names the page selects (via the <c>Tf</c>
/// operator), and whether the page paints with device-dependent colour. Rules use this to scope
/// checks to constructs that are exercised — matching veraPDF, which validates the <em>current</em>
/// graphics state rather than every resource that is merely present (see issues #118, #127, #128).
/// </summary>
/// <remarks>
/// Best-effort and defensive: the page content is decoded and tokenised with the reader's lexer;
/// inline-image sample data (<c>ID … EI</c>) is skipped; on any malformed or undecodable content the
/// scan stops and returns what it gathered. It is not a full content-stream interpreter — it tracks
/// only the operands needed for the questions above.
/// </remarks>
internal static class ContentStreamUsage
{
    private static readonly PdfName _contents = new("Contents");

    /// <summary>Scans <paramref name="page"/>'s content streams for graphics-state, colour,
    /// XObject-drawing, rendering-intent, selected-colour-space, selected-font, shading usage,
    /// and per-show text rendering mode.</summary>
    public static ContentUsage Analyze(PreflightContext context, PdfDictionary page)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        var drawnXObjects = new HashSet<string>(StringComparer.Ordinal);
        var renderingIntents = new HashSet<string>(StringComparer.Ordinal);
        var selectedColorSpaces = new HashSet<string>(StringComparer.Ordinal);
        var usedFonts = new HashSet<string>(StringComparer.Ordinal);
        var paintedShadings = new HashSet<string>(StringComparer.Ordinal);
        var usesDeviceColour = false;
        var textShows = new List<TextShow>();

        // Graphics-state stack for q/Q save-restore. Each entry is (fontResourceName, renderingMode).
        // Default rendering mode = 0 (fill text), default font = null (not yet selected).
        var gsStack = new Stack<(string? Font, int Mode)>();
        var currentFont = (string?)null;
        var currentRenderingMode = 0;

        var content = GetContentBytes(context, page);
        if (content is { Length: > 0 })
        {
            try
            {
                var lexer = new PdfLexer(content);
                // lastName tracks the most recent /Name token operand (for gs, Do, ri, cs/CS, Tf, sh).
                // lastInt tracks the most recent integer operand (for Tr).
                // lastStringBytes tracks the most recent string operand (for Tj, ', ").
                // None of these clear each other — they are independent tracking channels.
                // All three are cleared only when a keyword (operator) is encountered, because
                // an operator consumes all its pending operands.
                string? lastName = null;
                int? lastInt = null;
                byte[]? lastStringBytes = null;
                // Pending string operands for TJ: collected between [ and ]
                var tjStrings = new List<byte[]>();
                var inArray = false;

                while (!lexer.AtEnd)
                {
                    var token = lexer.NextToken();
                    if (token.Kind == TokenKind.EndOfInput)
                        break;

                    if (token.Kind == TokenKind.Name)
                    {
                        // Name token — update lastName; does NOT clear lastInt or lastStringBytes.
                        lastName = DecodeName(token.Raw.Span);
                        continue;
                    }

                    if (token.Kind == TokenKind.Integer)
                    {
                        // Integer token — update lastInt; does NOT clear lastName or lastStringBytes.
                        lastInt = ParseInt(token.Raw.Span);
                        // (number elements inside a TJ array are spacing adjustments — not text)
                        continue;
                    }

                    if (token.Kind == TokenKind.Real)
                    {
                        // Real number — Tr only takes an integer, so a real before Tr is not valid;
                        // clear lastInt so we don't misinterpret it as the Tr mode. Does NOT clear
                        // lastName or lastStringBytes.
                        lastInt = null;
                        continue;
                    }

                    if (token.Kind is TokenKind.LiteralString or TokenKind.HexString)
                    {
                        var bytes = DecodeStringBytes(token);
                        lastStringBytes = bytes;
                        if (inArray)
                            tjStrings.Add(bytes);
                        continue;
                    }

                    if (token.Kind == TokenKind.ArrayBegin)
                    {
                        inArray = true;
                        tjStrings.Clear();
                        continue;
                    }

                    if (token.Kind == TokenKind.ArrayEnd)
                    {
                        inArray = false;
                        continue;
                    }

                    if (token.Kind == TokenKind.Keyword)
                    {
                        var op = Encoding.Latin1.GetString(token.Raw.Span);
                        switch (op)
                        {
                            case "gs":
                                if (lastName is not null)
                                    applied.Add(lastName);
                                break;

                            case "rg" or "g" or "k" or "RG" or "G" or "K":
                                usesDeviceColour = true;
                                break;

                            case "Do":
                                if (lastName is not null)
                                    drawnXObjects.Add(lastName);
                                break;

                            case "ri":
                                if (lastName is not null)
                                    renderingIntents.Add(lastName);
                                break;

                            case "cs" or "CS":
                                if (lastName is not null)
                                    selectedColorSpaces.Add(lastName);
                                break;

                            case "Tf":
                                // /FontName size Tf — lastName holds the font resource name.
                                if (lastName is not null)
                                {
                                    usedFonts.Add(lastName);
                                    currentFont = lastName;
                                }
                                break;

                            case "sh":
                                if (lastName is not null)
                                    paintedShadings.Add(lastName);
                                break;

                            case "Tr":
                                // integer Tr — sets text rendering mode. lastInt is the operand.
                                // If lastInt is null the operand was indeterminate (e.g. a real such as
                                // "3.0 Tr", which the Real branch above cleared): set the mode to -1
                                // (unknown) rather than KEEP a possibly-visible value, so a later show
                                // is treated as "not a confirmed visible draw" — the false-positive-safe
                                // direction. A subsequent well-formed `N Tr` restores a known mode.
                                currentRenderingMode = lastInt ?? -1;
                                break;

                            case "q":
                                // Save graphics state.
                                gsStack.Push((currentFont, currentRenderingMode));
                                break;

                            case "Q":
                                // Restore graphics state.
                                if (gsStack.Count > 0)
                                    (currentFont, currentRenderingMode) = gsStack.Pop();
                                break;

                            case "Tj":
                                // string Tj
                                if (lastStringBytes is not null)
                                    textShows.Add(new TextShow(currentFont, currentRenderingMode, lastStringBytes));
                                break;

                            case "TJ":
                                // [ ... ] TJ — emit one TextShow per string element collected
                                foreach (var bytes in tjStrings)
                                    textShows.Add(new TextShow(currentFont, currentRenderingMode, bytes));
                                tjStrings.Clear();
                                break;

                            case "'":
                                // string ' — move to next line then show string
                                if (lastStringBytes is not null)
                                    textShows.Add(new TextShow(currentFont, currentRenderingMode, lastStringBytes));
                                break;

                            case "\"":
                                // aw ac string " — word spacing, char spacing, then show string.
                                // The string is in lastStringBytes.
                                if (lastStringBytes is not null)
                                    textShows.Add(new TextShow(currentFont, currentRenderingMode, lastStringBytes));
                                break;

                            case "ID":
                                SkipInlineImageData(lexer, content);
                                break;
                        }

                        // Keywords (operators) consume all pending operands; clear all tracking state.
                        // This preserves the original contract: each operator's operands are exactly
                        // the tokens that appeared since the previous operator.
                        lastName = null;
                        lastInt = null;
                        lastStringBytes = null;
                        // Note: inArray is NOT reset here because an ArrayEnd token closes it;
                        // a keyword inside an array (malformed content) is handled defensively.
                    }
                }
            }
            catch
            {
                // Malformed content — keep whatever was collected before the failure.
            }
        }

        return new ContentUsage(applied, usesDeviceColour, drawnXObjects, renderingIntents,
            selectedColorSpaces, usedFonts, paintedShadings, textShows);
    }

    /// <summary>The page's concatenated, decoded content-stream bytes (or null when empty/undecodable).
    /// Exposed for rules that need a deeper content scan than the operand tracking above.</summary>
    public static byte[]? GetPageContent(PreflightContext context, PdfDictionary page)
        => GetContentBytes(context, page);

    private static byte[]? GetContentBytes(PreflightContext context, PdfDictionary page)
    {
        var contentsObj = page.Get(_contents);
        using var ms = new MemoryStream();
        if (context.Resolve(contentsObj) is PdfArray array)
        {
            for (var i = 0; i < array.Count; i++)
                AppendStream(context, array[i], ms);
        }
        else
        {
            AppendStream(context, contentsObj, ms);
        }
        return ms.Length == 0 ? null : ms.ToArray();
    }

    private static void AppendStream(PreflightContext context, PdfObject? streamRef, MemoryStream ms)
    {
        var stream = context.ResolveStream(streamRef);
        if (stream is null)
            return;
        var bytes = context.DecodeStream(stream);
        if (bytes is null)
            return;
        ms.Write(bytes);
        ms.WriteByte((byte)'\n'); // separate concatenated content streams (ISO 32000-1 §7.8.2)
    }

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        // raw includes the leading '/'. Decode #XX escapes (ISO 32000-1 §7.3.5).
        var sb = new StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length; i++)
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

    // Decodes raw integer bytes (ASCII digits, optional leading sign) to int. Returns null on failure.
    private static int? ParseInt(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty) return null;
        var sign = 1;
        var i = 0;
        if (raw[0] == (byte)'-') { sign = -1; i = 1; }
        else if (raw[0] == (byte)'+') { i = 1; }
        if (i >= raw.Length) return null;
        var value = 0;
        for (; i < raw.Length; i++)
        {
            if (raw[i] < (byte)'0' || raw[i] > (byte)'9') return null;
            value = value * 10 + (raw[i] - '0');
        }
        return sign * value;
    }

    // Decodes the raw bytes of a LiteralString or HexString token to the content bytes.
    // For LiteralString this strips ( ) and resolves escape sequences.
    // For HexString this strips < > and converts hex pairs to bytes.
    private static byte[] DecodeStringBytes(Token token)
    {
        if (token.Kind == TokenKind.LiteralString)
            return PdfObjectParser.DecodeLiteralString(token.Raw).Bytes.ToArray();
        if (token.Kind == TokenKind.HexString)
            return PdfObjectParser.DecodeHexString(token.Raw).Bytes.ToArray();
        return [];
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    internal static void SkipInlineImageData(PdfLexer lexer, ReadOnlySpan<byte> content)
    {
        // After 'ID' a single whitespace precedes the raw samples, which run to a whitespace-delimited
        // 'EI'. Resume tokenising after that marker so binary samples are not mis-read as operators.
        var pos = lexer.Position;
        if (pos < content.Length && IsWhitespace(content[pos]))
            pos++;
        for (; pos + 1 < content.Length; pos++)
        {
            if (content[pos] == (byte)'E' && content[pos + 1] == (byte)'I'
                && (pos == 0 || IsWhitespace(content[pos - 1]))
                && (pos + 2 >= content.Length || IsWhitespace(content[pos + 2])))
            {
                lexer.Seek(pos + 2);
                return;
            }
        }
        lexer.Seek(content.Length);
    }

    private static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
}
