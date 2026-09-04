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
    /// this reports "(50 bytes)"). The one caller that decodes only far enough to excerpt an
    /// oversized keyword (<c>ContentInterpreter.HandleOperator</c>'s own dispatch site) passes the
    /// raw token's own length separately, since <paramref name="text"/> itself is already
    /// truncated there.
    /// </summary>
    internal static string Quote(string text, int byteLength) =>
        byteLength <= MaxChars
            ? text
            : $"{text[..MaxChars]}... ({byteLength} bytes)";
}
