// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance;

/// <summary>
/// The severity of a <see cref="PreflightAssertion"/>.
/// </summary>
public enum PreflightSeverity
{
    /// <summary>
    /// A requirement of the conformance level is violated. Any <see cref="Error"/> assertion
    /// makes <see cref="PreflightResult.IsCompliant"/> false.
    /// </summary>
    Error,

    /// <summary>
    /// A recommendation is not met, or a condition is suspicious but not strictly forbidden.
    /// Does not affect <see cref="PreflightResult.IsCompliant"/>.
    /// </summary>
    Warning,

    /// <summary>
    /// An informational note recorded during validation. Does not affect
    /// <see cref="PreflightResult.IsCompliant"/>.
    /// </summary>
    Info,
}
