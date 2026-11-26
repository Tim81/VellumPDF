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
| An image generated in code, fitted to the page | `DiagramImage`, `ManualWriter.FigurePage` |
| One chapter in Dutch, tagged with its own language | `Safety` |

## What it cannot do yet

The manual shows `[p. ?]` wherever a page number belongs. The workarounds for bold text in table
cells, the caption, the note boxes, the fonts, hand-typed heading numbers and spacing each have a
placeholder note on the page where it first matters (the colophon page for the last three). The
list below is not complete:

- Links to another part of the document. A cross-reference is plain text (#299, #600).
- A contents list or an index with page numbers (#294, #599).
- Runs, such as bold button names, and links inside a table cell (#692, #475).
- Numbered headings, which are typed by hand (#301).
- A header or footer that differs on the title page, or a page count that skips the front matter
  (#286, #365).
- A caption that stays with its figure. A fitted image fills the page, so the figure has none (#295).
- Named styles, so every element is given its style by hand (#292).
- A shaded paragraph. The note boxes are one-cell tables, which a screen reader announces as tables.
- Space after an element. A bottom margin adds none, so every gap is the top margin of the next
  element.
- The embedded fonts. The Standard14 package offers them to the Kernel's document only, so
  `ManualStyles` reads its resources by name, a name the package does not document.

The comments in the code say where each workaround sits.

## The checks

`tests/VellumPdf.Layout.Tests/SampleManualTests.cs` builds the manual and checks it: veraPDF for
PDF/A-2u, `qpdf --check`, the named destinations and bookmarks, the page labels, the Dutch passage and
its language tag, the colophon, the contents placeholders, byte-for-byte reproducibility, and that the
compiled sample names no renderer, canvas or `PdfDocument`.
