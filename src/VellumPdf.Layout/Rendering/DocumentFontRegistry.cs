// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Retained for any external callers; the internal Draw path now flows the
/// document reference explicitly via <see cref="Core.DrawContext.GetFont"/>.
/// </summary>
internal static class DocumentFontRegistry
{
    // No thread-static state. Font access goes through DrawContext.GetFont().
}
