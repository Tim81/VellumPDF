// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>An image element that can be placed in document flow.</summary>
public sealed class LayoutImage
{
    public PdfImageXObject Image { get; }
    public double? Width { get; init; }  // null = fit to available width
    public double? Height { get; init; }  // null = maintain aspect ratio
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    public LayoutImage(PdfImageXObject image) => Image = image;
}
