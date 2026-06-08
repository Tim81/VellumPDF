// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Encryption;

/// <summary>
/// PDF access permissions for the Standard security handler (ISO 32000-2 Table 22).
/// Each flag corresponds to a bit in the 32-bit /P integer written to the /Encrypt dict.
/// </summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>No permissions granted.</summary>
    None = 0,

    /// <summary>Print the document (low resolution or degraded).</summary>
    Print = 1 << 2,

    /// <summary>Modify the document (other than annotations, forms, signatures).</summary>
    Modify = 1 << 3,

    /// <summary>Copy or extract text and graphics.</summary>
    Copy = 1 << 4,

    /// <summary>Add or modify text annotations and fill in forms.</summary>
    Annotate = 1 << 5,

    /// <summary>Fill in existing interactive form fields.</summary>
    FillForms = 1 << 8,

    /// <summary>Extract text and graphics (disability accessibility support).</summary>
    Extract = 1 << 9,

    /// <summary>Assemble the document (insert/delete pages, create bookmarks).</summary>
    Assemble = 1 << 10,

    /// <summary>Print the document in high fidelity (high-resolution).</summary>
    PrintHighRes = 1 << 11,

    /// <summary>All permissions granted.</summary>
    All = Print | Modify | Copy | Annotate | FillForms | Extract | Assemble | PrintHighRes,
}
