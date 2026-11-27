# VellumPdf sample: a product manual

This project writes a user manual of about thirty pages for a made-up air purifier, the Aerolith AL-5.
It is a PDF/A-2u file built with the `VellumPdf.Layout` public API and nothing else: no `IRenderer`, no
`PdfCanvas` and no `PdfDocument`. The point is to show what a long, structured document looks like in
code, and to show where the Layout API stops short, so the gaps are visible while they can still be
closed.

## Run it

From the repository root:

```shell
dotnet run --project samples/VellumPdf.Samples.Manual -c Release -f net10.0 -- manual.pdf
```

The argument is the output path. Without it the file is `aerolith-al5-manual.pdf` in the current
directory. The `-f` is needed because the repository builds every project for net10.0 and net11.0
(`Directory.Build.props`), and `dotnet run` refuses to pick one; `-f net11.0` works too. The sample
keeps both targets because the Layout test project, which builds and checks it, runs on both. The
document date and the file identifier are fixed, so two runs, in separate processes, write the same
bytes.

## What the manual uses

| Feature | Where in the sample |
| --- | --- |
| PDF/A-2u, embedded Liberation fonts, tagged output | `ManualDocument.Build`, `ManualStyles` |
| Title page, colophon with an external link, contents | `FrontMatter` |
| Roman page labels for the front matter, decimal ones from chapter 1 | `ManualDocument.Build` |
| Chapters that start on a right-hand page, mirrored margins | `ManualWriter.Chapter` |
| Headings with bookmarks and named destinations | `ManualWriter.AddHeading` |
| Running header with the chapter title, footer with page of pages | `ManualDocument.Build` |
| Paragraphs with bold, italic and monospace runs | `ManualWriter.Runs` |
| Bulleted, numbered and nested lists | `ManualWriter.Bullets`, `Steps`, `StepsWithNested` |
| Tables with a repeating header row and line breaks in cells | `ManualWriter.Table`, `Troubleshooting` |
| Command blocks with tab characters | `ManualWriter.CommandBlock`, `Network` |
| Tab stops with dot leaders | `ManualWriter.LeaderLine`, `Care` |
| Contents with links and printed page numbers (`TextRun.PageReference`) | `FrontMatter`, `ManualWriter.ContentsLine` |
| Cross-references as links with the page printed, in English and Dutch | `ManualWriter.Runs` |
| An index whose entries link to the places they name | `Appendices`, `ManualWriter.IndexLine` |
| Runs, bold and linked, inside table cells | `ManualWriter.Table`, `ManualWriter.Callout` |
| Space after an element, a keep-together command block | `FrontMatter`, `ManualWriter.CommandBlock` |
| The bundled Liberation faces, registered from a Layout document | `ManualStyles` |
| An image generated in code, fitted to the page | `DiagramImage`, `ManualWriter.FigurePage` |
| One chapter in Dutch, tagged with its own language | `Safety` |

## What it cannot do yet

The note boxes, the caption, heading numbers, the contents, the index and the position of the title
page each have a "Placeholder" note on the page where they first matter. The other items are named
here only. The list is not complete:

- Numbered headings, which are typed by hand (#301).
- A contents list and an index generated from the document. Both are written by hand, with the
  library printing the page numbers (#294).
- A caption that stays with its figure. A fitted image fills the page, so the figure has none (#295).
- A shaded paragraph. The note boxes are one-cell tables, which a screen reader announces as tables
  (#703).
- A header or footer that differs on the title page, or a page count that skips the front matter
  (#286, #365).
- Placing an element at a position on the page. The title and colophon text sit at a distance from
  the top set by a top margin (#313).
- Space below an element in the body. A bottom margin adds none, so the body's gaps are the top margin
  of the next element; the title page and colophon use `SpaceAfter` (#704).
- Named styles, so every element is given its style by hand (#292).
- Loading an image without naming a Kernel type (#291), and a Layout-only program still names nine
  Kernel types, which a test pins (#706).

The comments in the code say where each workaround sits.

## The checks

`tests/VellumPdf.Layout.Tests/SampleManualTests.cs` builds the manual and checks it: veraPDF for
PDF/A-2u, `qpdf --check`, the named destinations and bookmarks, the page labels, the Dutch passage and
its language tag, the colophon, that every internal link lands on the heading it names and every
printed page number is the label of that heading's page, byte-for-byte reproducibility, and that the
compiled sample names no renderer, canvas or `PdfDocument`.
