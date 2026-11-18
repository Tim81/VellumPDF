// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

// The rows pass nulls on purpose where the library annotates a non-null parameter.
#nullable disable

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A null that 1c61fa6 stored and dereferenced only in some shapes is refused in exactly those shapes,
/// with <see cref="ArgumentNullException"/> where it threw <see cref="NullReferenceException"/>, and
/// the document that saved there still saves. Each row is a shape measured on 1c61fa6: the second
/// element is whether it saved. The rows that threw there throw here, and none that saved throws
/// (#484), which is the rule "what was already refused stays refused" together with "a valid file
/// is kept". A null that always crashed on 1c61fa6 is refused at the call; see
/// <c>NullArgumentTests</c>.
/// </summary>
public sealed class NullDeferralTests
{
    private static TextRun R(string text) => new(text, null);

    private static readonly (string Name, bool Saved, Func<Document> Build)[] _rows =
    [
        ("run null style, text Hello", false, () => { var d = new Document(); d.Add(new Paragraph(new[]{R("Hello")})); return d; }),
        ("run null style, empty text", true, () => { var d = new Document(); d.Add(new Paragraph(new[]{R("")})); return d; }),
        ("run null style, spaces", true, () => { var d = new Document(); d.Add(new Paragraph(new[]{R("   ")})); return d; }),
        ("run null style, tab", true, () => { var d = new Document(); d.Add(new Paragraph(new[]{R("\t")})); return d; }),
        ("run null style, nbsp", false, () => { var d = new Document(); d.Add(new Paragraph(new[]{R(" ")})); return d; }),
        ("run null style, newline only", true, () => { var d = new Document(); d.Add(new Paragraph(new[]{R("\n")})); return d; }),
        ("run null style, a newline b", false, () => { var d = new Document(); d.Add(new Paragraph(new[]{R("a\nb")})); return d; }),
        ("run null style, ws between styled", true, () => { var d = new Document(); d.Add(new Paragraph(new[]{new TextRun("a",TextStyle.Default),R(" "),new TextRun("b",TextStyle.Default)})); return d; }),
        ("run null style, x between styled", false, () => { var d = new Document(); d.Add(new Paragraph(new[]{new TextRun("a",TextStyle.Default),R("x"),new TextRun("b",TextStyle.Default)})); return d; }),
        ("run null style, paragraph never added", true, () => { var d = new Document(); _=new Paragraph(new[]{R("Hello")}); d.Add(new Paragraph("ok")); return d; }),
        ("cell null auto 1 col", false, () => { var d = new Document(); var t=new TableElement(); t.AddRow().AddCell(new Cell(null)); d.Add(t); return d; }),
        ("cell null explicit widths all (60)", true, () => { var d = new Document(); var t=new TableElement().SetColumnWidths(60); t.AddRow().AddCell(new Cell(null)); d.Add(t); return d; }),
        ("cell null 2 cols widths (60,60)", true, () => { var d = new Document(); var t=new TableElement().SetColumnWidths(60,60); var r=t.AddRow(); r.AddCell("a"); r.AddCell(new Cell(null)); d.Add(t); return d; }),
        ("cell null widths (60,0), null in explicit col", false, () => { var d = new Document(); var t=new TableElement().SetColumnWidths(60,0); var r=t.AddRow(); r.AddCell(new Cell(null)); r.AddCell("b"); d.Add(t); return d; }),
        ("cell null widths (60,0), null in auto col", false, () => { var d = new Document(); var t=new TableElement().SetColumnWidths(60,0); var r=t.AddRow(); r.AddCell("a"); r.AddCell(new Cell(null)); d.Add(t); return d; }),
        ("cell null second row, widths (60,60)", true, () => { var d = new Document(); var t=new TableElement().SetColumnWidths(60,60); t.AddRow().AddCell("a").AddCell("b"); t.AddRow().AddCell(new Cell(null)); d.Add(t); return d; }),
        ("cell null auto, table not drawn (doc top -inf)", false, () => { var d = new Document(){Margins=new EdgeInsets(double.NegativeInfinity,72,72,72)}; var t=new TableElement(); t.AddRow().AddCell(new Cell(null)); d.Add(t); return d; }),
        ("cell null explicit (60), null cell beyond columns", false, () => { var d = new Document(); var t=new TableElement().SetColumnWidths(60); var r=t.AddRow(); r.AddCell("a"); r.AddCell(new Cell(null)); d.Add(t); return d; }),
        ("cell null auto, null cell beyond columns", false, () => { var d = new Document(); var t=new TableElement(); t.AddRow().AddCell("a"); var r=t.AddRow(); r.AddCell("b"); r.AddCell(new Cell(null)); d.Add(t); return d; }),
        ("cell null auto via Row.AddCell((string)null)", false, () => { var d = new Document(); var t=new TableElement(); t.AddRow().AddCell((string)null); d.Add(t); return d; }),
        ("dict Set(k,null) then Set(k,v), written", true, () => { var d = new Document(); var x=new PdfDictionary(); x.Set(new PdfName("K"),(PdfObject)null); x.Set(new PdfName("K"),1L); using var m=new MemoryStream(); x.WriteTo(new VellumPdf.IO.PdfWriter(m)); d.Add("hello"); return d; }),
        ("dict Set(k,null) then written", false, () => { var d = new Document(); var x=new PdfDictionary(); x.Set(new PdfName("K"),(PdfObject)null); using var m=new MemoryStream(); x.WriteTo(new VellumPdf.IO.PdfWriter(m)); d.Add("hello"); return d; }),
        ("dict Set(k,null) never written", true, () => { var d = new Document(); var x=new PdfDictionary(); x.Set(new PdfName("K"),(PdfObject)null); d.Add("hello"); return d; }),
        ("band null template assigned then replaced", true, () => { var d = new Document(); d.Header=new RunningBand(null); d.Header=new RunningBand("h"); d.Add("hello"); return d; }),
        ("band null template assigned then cleared", true, () => { var d = new Document(); d.Header=new RunningBand(null); d.Header=null; d.Add("hello"); return d; }),
        ("band null template header kept", false, () => { var d = new Document(); d.Header=new RunningBand(null); d.Add("hello"); return d; }),
        ("band null template footer kept", false, () => { var d = new Document(); d.Footer=new RunningBand(null); d.Add("hello"); return d; }),
        ("SetHeader(null) kept", false, () => { var d = new Document(); d.SetHeader(null); d.Add("hello"); return d; }),
        ("listitem null child on grandchild", true, () => { var d = new Document(); var c=new ListItem("c"); c.AddChild((ListItem)null); d.Add(new ListElement().Add(new ListItem("p").AddChild(c))); return d; }),
        ("listitem null child on top-level item", false, () => { var d = new Document(); d.Add(new ListElement().Add(new ListItem("p").AddChild((ListItem)null))); return d; }),
        ("listitem null text top-level", false, () => { var d = new Document(); d.Add(new ListElement().Add(new ListItem(null))); return d; }),
        ("listitem null text child", false, () => { var d = new Document(); d.Add(new ListElement().Add(new ListItem("p").AddChild((string)null))); return d; }),
        ("listitem null text grandchild", true, () => { var d = new Document(); var c=new ListItem("c"); c.AddChild((string)null); d.Add(new ListElement().Add(new ListItem("p").AddChild(c))); return d; }),
        ("listitem null child, list never added", true, () => { var d = new Document(); _=new ListElement().Add(new ListItem("p").AddChild((ListItem)null)); d.Add("ok"); return d; }),
    ];

    public static TheoryData<string> Names()
    {
        var rows = new TheoryData<string>();
        foreach (var r in _rows) rows.Add(r.Name);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void NullShape_savesWhere1c61fa6Saved_andIsRefusedWhereItThrew(string name)
    {
        var row = _rows.Single(r => r.Name == name);
        using var ms = new MemoryStream();

        // The build is inside the call: a few rows write a Kernel object while they build.
        void BuildAndSave()
        {
            using var doc = row.Build();
            doc.Save(ms);
        }

        if (row.Saved)
        {
            BuildAndSave();
            Assert.True(ms.Length > 0);
        }
        else
        {
            Assert.Throws<ArgumentNullException>(BuildAndSave);
        }
    }
}
