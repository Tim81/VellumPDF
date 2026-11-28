// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using static VellumPdf.Layout.Tests.FontReportSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Under PDF/A-2u, PDF/A-2a and PDF/UA-1 the save names a glyph that only U+FEFF reaches U+2060 in
/// the font's ToUnicode map, and reports it as
/// <see cref="LayoutDiagnosticCode.ToUnicodeValueSubstituted"/> (#752). The map's bytes are pinned
/// in the Kernel tests; this class pins the report.
/// </summary>
public sealed class ToUnicodeSubstitutionReportTests
{
    private const string Message =
        "U+FEFF is written to the font's ToUnicode map as U+2060, because a ToUnicode value may not be "
        + "U+0000, U+FEFF or U+FFFE; text extraction reads U+2060 where the text held U+FEFF.";

    private static Document WithText(PdfConformance claim, params string[] paragraphs)
    {
        var doc = NewDocument(claim);
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVuBytes), FontSize = 11 };
        foreach (var text in paragraphs)
            doc.Add(new Paragraph(text, style));
        return doc;
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2u, "ISO 19005-2:2011, 6.2.11.7.2")]
    [InlineData(PdfConformance.PdfA2a, "ISO 19005-2:2011, 6.2.11.7.2")]
    [InlineData(PdfConformance.PdfUA1, "ISO 14289-1:2014, 7.21.7")]
    public void ASaveThatNamesU_FEFFAsAWordJoiner_reportsItOnce_withTheClauseOfTheClaim(PdfConformance claim, string clause)
    {
        // Two paragraphs and two characters in one: the report is one item per character, and the
        // font it concerns is counted, not the occurrences.
        var doc = WithText(claim, "a﻿b﻿c", "d﻿e");
        Save(doc);

        var item = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ToUnicodeValueSubstituted);
        Assert.Equal(LayoutDiagnosticSeverity.Warning, item.Severity);
        Assert.Equal(Message, item.Message);
        Assert.Equal(clause, item.Clause);
        Assert.Equal(LayoutElementKind.Document, item.ElementKind);
        Assert.Null(item.ElementIndex);
        Assert.Null(item.PageNumber);
        Assert.Equal(1, item.Count);
    }

    [Theory]
    [InlineData(PdfConformance.None)]
    [InlineData(PdfConformance.PdfA2b)]
    public void WhereNoLevelNeedsToUnicode_nothingIsReported(PdfConformance claim)
    {
        var doc = WithText(claim, "a﻿b");
        Save(doc);

        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ToUnicodeValueSubstituted);
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2u)]
    [InlineData(PdfConformance.PdfA2a)]
    [InlineData(PdfConformance.PdfUA1)]
    public void TextWithoutTheCharacter_reportsNothing(PdfConformance claim)
    {
        var doc = WithText(claim, "plain text");
        Save(doc);

        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ToUnicodeValueSubstituted);
    }

    [Fact]
    public void AWarning_doesNotStopAStrictSave()
    {
        var doc = WithText(PdfConformance.PdfA2u, "a﻿b");
        doc.StrictDiagnostics = true;

        Save(doc);

        Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ToUnicodeValueSubstituted);
    }
}
