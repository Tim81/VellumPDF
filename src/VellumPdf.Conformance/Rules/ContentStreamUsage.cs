// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules;

/// <summary>
/// A minimal content-stream operator scan. It reports which graphics-state (<c>/ExtGState</c>)
/// resources a page actually applies (via the <c>gs</c> operator), which XObjects it actually paints
/// (via the <c>Do</c> operator), and whether the page paints with device-dependent colour. Rules use
/// this to scope checks to constructs that are exercised — matching veraPDF, which validates the
/// <em>current</em> graphics state rather than every resource that is merely present (see issues
/// #127, #128).
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
    /// XObject-drawing, and rendering-intent usage.</summary>
    public static (HashSet<string> AppliedExtGStates, bool UsesDeviceColour, HashSet<string> DrawnXObjects,
        HashSet<string> RenderingIntents) Analyze(PreflightContext context, PdfDictionary page)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        var drawnXObjects = new HashSet<string>(StringComparer.Ordinal);
        var renderingIntents = new HashSet<string>(StringComparer.Ordinal);
        var usesDeviceColour = false;

        var content = GetContentBytes(context, page);
        if (content is { Length: > 0 })
        {
            try
            {
                var lexer = new PdfLexer(content);
                string? lastName = null;
                while (!lexer.AtEnd)
                {
                    var token = lexer.NextToken();
                    if (token.Kind == TokenKind.EndOfInput)
                        break;

                    if (token.Kind == TokenKind.Name)
                    {
                        lastName = DecodeName(token.Raw.Span);
                        continue;
                    }

                    if (token.Kind == TokenKind.Keyword)
                    {
                        var op = Encoding.Latin1.GetString(token.Raw.Span);
                        if (op == "gs")
                        {
                            if (lastName is not null)
                                applied.Add(lastName);
                        }
                        else if (op is "rg" or "g" or "k" or "RG" or "G" or "K")
                        {
                            usesDeviceColour = true;
                        }
                        else if (op == "Do")
                        {
                            if (lastName is not null)
                                drawnXObjects.Add(lastName);
                        }
                        else if (op == "ri")
                        {
                            // The `ri` operator sets the current rendering intent to its name operand.
                            if (lastName is not null)
                                renderingIntents.Add(lastName);
                        }
                        else if (op == "ID")
                        {
                            SkipInlineImageData(lexer, content);
                        }
                    }

                    // Any operator or non-name operand clears the pending name (operators consume operands).
                    lastName = null;
                }
            }
            catch
            {
                // Malformed content — keep whatever was collected before the failure.
            }
        }

        return (applied, usesDeviceColour, drawnXObjects, renderingIntents);
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

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    private static void SkipInlineImageData(PdfLexer lexer, ReadOnlySpan<byte> content)
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
