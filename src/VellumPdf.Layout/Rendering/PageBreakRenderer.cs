// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// A renderer that can ask for a page break before it is placed. <see cref="DocumentRenderer"/>
/// reads <see cref="BreakBefore"/> from each top-level renderer it places, and for a
/// <see cref="PageBreakRenderer"/> it never calls <see cref="IRenderer.Layout"/>.
/// </summary>
/// <remarks>
/// Implement it explicitly. The built-in renderers are public classes, so an implicit
/// implementation would be a new public member.
/// </remarks>
internal interface IPageBreakSource
{
    PageBreakMode BreakBefore { get; }
}

/// <summary>
/// What <see cref="Document.Add(PageBreak)"/> adds. It sits in the document's content so the
/// position of every element, which <see cref="LayoutDiagnostic.ElementIndex"/> counts, stays the
/// order the caller added them in. The placement loop takes its mode and skips it.
/// </summary>
internal sealed class PageBreakRenderer(PageBreak pageBreak) : IRenderer, IPageBreakSource
{
    internal PageBreak PageBreak { get; } = pageBreak;

    PageBreakMode IPageBreakSource.BreakBefore =>
        PageBreak.Recto ? PageBreakMode.Recto : PageBreakMode.Page;

    // The placement loop never calls this. A caller that reaches it anyway gets a zero-height box
    // that places nothing.
    public LayoutResult Layout(LayoutContext context) =>
        LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, context.Area.Width, 0));

    public void Draw(DrawContext ctx)
    {
    }
}
