// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// The core two-phase rendering contract.
/// Layout determines sizing and splitting; Draw emits PDF operators.
/// </summary>
public interface IRenderer
{
    /// <summary>
    /// Phase 1: determine how much of this element fits in <paramref name="context"/>.
    /// Must not mutate any state visible to the caller (pure computation).
    /// </summary>
    LayoutResult Layout(LayoutContext context);

    /// <summary>
    /// Phase 2: emit PDF operators into <paramref name="context"/>.
    /// Called only after a successful Layout (Full or Partial).
    /// </summary>
    void Draw(DrawContext context);
}
