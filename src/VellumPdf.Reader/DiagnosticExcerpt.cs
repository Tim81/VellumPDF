// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// Bounds how much of a producer-controlled name or keyword a retained diagnostic quotes. A
/// diagnostic's job is to identify a malformed token, not to carry the whole thing: neither
/// <c>PdfLexer.ReadKeyword</c> nor <see cref="PdfName"/> bounds a token's own length, and Annex
/// C.1 puts no bound on either ("In general, this PDF standard does not restrict the size or
/// quantity of things described in the PDF file format"; Table C.1's 127-byte name length is only
/// informative). A <see cref="PdfReaderDiagnostic"/> is retained for the reader's lifetime
/// (<see cref="DiagnosticSink"/>), so quoting an oversized token whole would turn one attacker- or
/// corruption-controlled byte run into a comparably sized permanent allocation, once per
/// (code, object, page) the sink's dedupe key admits (#402).
/// </summary>
internal static class DiagnosticExcerpt
{
    internal const int MaxChars = 32;

    /// <summary>Quotes at most <see cref="MaxChars"/> of <paramref name="text"/>.</summary>
    internal static string Quote(string text) => Quote(text, text.Length);

    /// <summary>
    /// Quotes at most <see cref="MaxChars"/> of <paramref name="text"/>. <paramref name="byteLength"/>
    /// is the decoded value's own byte length (Latin1: one char per byte), not necessarily the raw
    /// token's: for a <see cref="PdfName"/> it is just <c>text.Length</c>, but a name whose raw
    /// token used one or more <c>#xx</c> escapes (§7.3.5) decodes to fewer bytes than it was
    /// written in, so the raw token can run longer than <paramref name="byteLength"/> reports
    /// (<c>'/' + 40 'B' + '#20' x10</c> is a 71-byte raw token whose decoded Value is 50 bytes, and
    /// this reports "(50 bytes)"). A caller that still has the raw token in hand — a bare keyword or
    /// numeric literal, neither of which has a <c>#xx</c> escape to decode — passes the raw span's
    /// own length here instead of relying on the single-argument overload's <c>text.Length</c>
    /// default; this is a convention at those call sites, not a correctness requirement, since the
    /// two lengths already agree for them. A caller quoting a <see cref="PdfName"/>'s decoded
    /// <c>Value</c> instead uses the single-argument overload, because an escape in the raw token
    /// would make the two lengths disagree and the decoded value's own length is the correct one to
    /// report. <c>ContentInterpreter.HandleOperator</c>'s own dispatch site is the one caller where
    /// neither reason applies cleanly: it decodes only far enough to excerpt an oversized keyword, so
    /// <paramref name="text"/> itself is already truncated and its own length would undercount.
    /// <para>
    /// Precondition: <paramref name="byteLength"/> above <see cref="MaxChars"/> must imply
    /// <paramref name="text"/> is at least <see cref="MaxChars"/> characters long, or
    /// <c>text[..MaxChars]</c> throws instead of diagnosing. Every current caller decodes as Latin1
    /// (one byte, one char), so this holds by construction; a future caller decoding as UTF-8 (or
    /// any variable-width encoding) and passing the encoded byte length here — rather than the
    /// decoded character count — would violate it.
    /// </para>
    /// </summary>
    internal static string Quote(string text, int byteLength) =>
        byteLength <= MaxChars
            ? text
            : $"{text[..MaxChars]}... ({byteLength} bytes)";
}
