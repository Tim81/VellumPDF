// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;

namespace VellumPdf.Reader.Content;

/// <summary>
/// Receives the events <see cref="ContentInterpreter.Run"/> produces while walking a page's content
/// stream (ISO 32000-2 §7.8.2). A caller reads <see cref="ContentInterpreter.GraphicsState"/> and
/// <see cref="ContentInterpreter.TextState"/> from inside these callbacks to see the state as of the
/// event; both are mutated in place, so a callback that needs a value after the interpreter has
/// moved on must copy it out rather than hold the reference.
/// </summary>
internal interface IContentVisitor
{
    /// <summary>
    /// Called for every recognised operator (Annex A Table A.1) the interpreter accepts, i.e. after
    /// its own operand-count and stack-discipline checks pass; a malformed or unrecognised operator
    /// never reaches this callback (see <see cref="PdfReaderDiagnosticCode.OperandStackMalformed"/>
    /// and <see cref="PdfReaderDiagnosticCode.UnknownOperator"/>).
    /// </summary>
    /// <param name="operatorName">The operator keyword, e.g. <c>"Tj"</c> or <c>"re"</c>.</param>
    /// <param name="operands">
    /// This operator's operands, in the order they appeared. Owned by the interpreter and reused for
    /// the next operator once this call returns, so a callback that needs an operand's value after
    /// returning must copy it out, not hold the list.
    /// </param>
    /// <param name="offset">The byte offset, within the buffer currently being interpreted (the
    /// page's own concatenated content, or the current Form XObject's own content), of the operator
    /// keyword's first byte.</param>
    void OnOperator(string operatorName, IReadOnlyList<PdfObject> operands, int offset);

    /// <summary>
    /// Called once an inline image (ISO 32000-2 §8.9.7) has been fully delimited and its dictionary
    /// decoded, with Table 91/92 abbreviations already expanded to their full names. Not called at all
    /// for an image this interpreter could not delimit or decode
    /// (see <see cref="PdfReaderDiagnosticCode.InlineImageMalformed"/>).
    /// </summary>
    /// <param name="dictionary">The inline image's key/value pairs, with every Table 91/92
    /// abbreviation already expanded (e.g. <c>/W</c> to <c>/Width</c>, <c>/CS /G</c> to
    /// <c>/ColorSpace /DeviceGray</c>).</param>
    /// <param name="data">The image's own (still filtered, undecoded) sample data: the bytes between
    /// <c>ID</c> and <c>EI</c>, excluding the delimiting white space.</param>
    /// <param name="offset">The byte offset of the <c>BI</c> operator that began this image.</param>
    void OnInlineImage(PdfDictionary dictionary, ReadOnlyMemory<byte> data, int offset);

    /// <summary>
    /// Called immediately before the interpreter recurses into a Form XObject's own content, once a
    /// <c>Do</c> operator (already reported through <see cref="OnOperator"/>) has been resolved to a
    /// <c>/Subtype /Form</c> stream and every recursion guard (depth, cycle, per-page budget) has
    /// passed. Matched by exactly one <see cref="OnFormEnd"/> call once the form's own content
    /// finishes interpreting, even if that content raises further nested <see cref="OnFormBegin"/>
    /// calls of its own in between.
    /// </summary>
    /// <param name="formDictionary">The form XObject's own stream dictionary.</param>
    /// <param name="formMatrix">The form's <c>/Matrix</c> (ISO 32000-2 §8.10.2 Table 93), or
    /// <see cref="Matrix.Identity"/> when absent or malformed. The interpreter does not compose this
    /// with the CTM itself; that is left to the caller.</param>
    /// <param name="boundingBox">The form's <c>/BBox</c> (Table 93, Required), or
    /// <see langword="null"/> when absent or malformed.</param>
    /// <param name="objectNumber">The form stream's own indirect object number.</param>
    /// <param name="offset">The byte offset of the <c>Do</c> operator that invoked this form, in the
    /// buffer the interpreter was walking at the time, i.e. the INVOKING stream's own offset space,
    /// not the form's.</param>
    void OnFormBegin(
        PdfDictionary formDictionary, Matrix formMatrix, PdfRectangle? boundingBox, int objectNumber,
        int offset);

    /// <summary>Called once a Form XObject's own content has finished interpreting, matching the
    /// most recent unmatched <see cref="OnFormBegin"/> call.</summary>
    /// <param name="objectNumber">The same form stream object number <see cref="OnFormBegin"/>
    /// reported.</param>
    void OnFormEnd(int objectNumber);
}
