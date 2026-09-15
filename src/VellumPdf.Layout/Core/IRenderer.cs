// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// The core two-phase rendering contract.
/// Layout determines sizing and splitting; Draw emits PDF operators.
/// </summary>
/// <remarks>
/// A custom implementation whose overflow never advances hits a cap of 50,000 page
/// continuations. See <see cref="Layout"/>.
/// </remarks>
public interface IRenderer
{
    /// <summary>
    /// Phase 1: determine how much of this element fits in <paramref name="context"/>.
    /// Must not mutate any state visible to the caller (pure computation).
    /// </summary>
    /// <remarks>
    /// An overflow renderer that never advances is accepted here. Pagination stops it at
    /// <b>50,000</b> continuations and throws <see cref="InvalidOperationException"/> from
    /// <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/>. Return <see cref="LayoutResult.Full"/>
    /// or a smaller overflow, not a copy of yourself that occupies the same height again.
    /// </remarks>
    LayoutResult Layout(LayoutContext context);

    /// <summary>
    /// Phase 2: emit PDF operators into <paramref name="context"/>.
    /// Called only after a successful Layout (Full or Partial).
    /// </summary>
    /// <remarks>
    /// Implementations must not assume Layout ran on this instance in the same call. A
    /// <see cref="LayoutResult.Partial"/> split renderer is the object that Draw sees.
    /// </remarks>
    void Draw(DrawContext context);
}
