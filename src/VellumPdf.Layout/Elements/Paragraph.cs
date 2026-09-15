// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A block of text. Supports uniform style (single run) and mixed-style inline runs.
/// Wraps text across lines and paginates automatically.
/// </summary>
public sealed class Paragraph
{
    private readonly List<TextRun> _runs;

    // ── Back-compat single-run properties ────────────────────────────────────

    /// <summary>The text of the first (or only) run. Valid for single-run paragraphs.</summary>
    public string Text => _runs.Count == 1 ? _runs[0].Text : string.Concat(_runs.Select(r => r.Text));

    /// <summary>The style of the first (or only) run.</summary>
    public TextStyle Style => _runs.Count > 0 ? _runs[0].Style : TextStyle.Default;

    /// <summary>All inline text runs in this paragraph (always at least one entry).</summary>
    public IReadOnlyList<TextRun> Runs => _runs;

    /// <summary>Margins around the paragraph.</summary>
    /// <remarks>
    /// <b>Attention</b>: this inset is <b>not</b> validated. <see cref="LineSeparator.Margins"/>
    /// and <see cref="Table.Cell.Padding"/> are the only insets this package checks; every other
    /// one, including this, reaches the geometry as given.
    /// <para>So a non-finite inset is not refused on your behalf. What happens instead depends on
    /// where the arithmetic lands, not on which member you set, and none of the outcomes is a
    /// refusal naming this property: the value can reach the content stream as a token no reader
    /// can parse, or trip a later geometry check that blames something else. Nothing reports it
    /// either way.</para>
    /// <para>Do <b>not</b> pass a negative inset either, and do not read one as a way to position
    /// or resize. It is arithmetic on the available area rather than a placement instruction, so
    /// what a renderer then does with that area is what you get: some carry the content off the
    /// page, others absorb the value and draw exactly as they would at zero. Nothing is refused
    /// and nothing is reported. A later major version will reject both.</para>
    /// </remarks>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Horizontal alignment of the paragraph text.</summary>
    /// <remarks>
    /// <see cref="HorizontalAlignment.Justify"/> is honoured for wrapped lines; the last line
    /// of a paragraph stays left-aligned. That is the one consumer in this package that
    /// justifies. A cell, image, pie chart or running band treats Justify as left.
    /// </remarks>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>
    /// Optional per-element language override (BCP 47 / RFC 5646, e.g. <c>"en-US"</c>).
    /// When set and the document is tagged, written as <c>/Lang</c> on the struct element.
    /// </summary>
    /// <remarks>
    /// The string is not validated. Same as <see cref="Document.Language"/>: an ill-formed tag
    /// is written as given.
    /// </remarks>
    public string? Language { get; init; }

    // ── Constructors ─────────────────────────────────────────────────────────

    /// <summary>Creates a single-run paragraph with uniform style.</summary>
    public Paragraph(string text, TextStyle? style = null)
    {
        _runs = [new TextRun(text, style ?? TextStyle.Default)];
    }

    /// <summary>Creates a mixed-style paragraph from a sequence of runs.</summary>
    public Paragraph(IEnumerable<TextRun> runs)
    {
        _runs = [.. runs];
        if (_runs.Count == 0)
            _runs = [new TextRun(string.Empty, TextStyle.Default)];
    }

    // ── Fluent builder ───────────────────────────────────────────────────────

    /// <summary>Appends a run with the given text and optional style. Returns this paragraph.</summary>
    public Paragraph Add(string text, TextStyle? style = null)
    {
        _runs.Add(new TextRun(text, style ?? Style));
        return this;
    }
}
