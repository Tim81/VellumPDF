// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// The predefined CMap names of ISO 32000-1:2008, Table 118 (including <c>Identity-H</c> /
/// <c>Identity-V</c>). A composite font's <c>/Encoding</c> that names one of these need not embed
/// the CMap program; any other name must instead be an embedded CMap stream.
/// </summary>
/// <remarks>
/// This is the single authoritative copy, shared by every rule that distinguishes predefined CMaps
/// from embedded ones — PDF/A-2 §6.2.11.3.x (<see cref="FontStructureRule"/>,
/// <see cref="CMapContentRule"/>, <see cref="CidRangeRule"/>) and PDF/UA-1 §7.21.3.x
/// (<c>UaCMapRule</c>, <c>UaCidSystemInfoRule</c>). Keeping one copy removes the drift risk that an
/// out-of-sync list would create: a missing name would make a conforming predefined CMap a false
/// positive.
/// </remarks>
internal static class PredefinedCMaps
{
    /// <summary>The predefined CMap names (ordinal comparison). Read-only — do not mutate.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "Identity-H", "Identity-V",
        "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H", "GBK-EUC-V",
        "GBKp-EUC-H", "GBKp-EUC-V", "GBK2K-H", "GBK2K-V", "UniGB-UCS2-H", "UniGB-UCS2-V",
        "UniGB-UTF16-H", "UniGB-UTF16-V",
        "B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V", "ETen-B5-H", "ETen-B5-V",
        "ETenms-B5-H", "ETenms-B5-V", "CNS-EUC-H", "CNS-EUC-V", "UniCNS-UCS2-H", "UniCNS-UCS2-V",
        "UniCNS-UTF16-H", "UniCNS-UTF16-V",
        "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90msp-RKSJ-V", "90pv-RKSJ-H",
        "Add-RKSJ-H", "Add-RKSJ-V", "EUC-H", "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V",
        "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V",
        "UniJIS-UTF16-H", "UniJIS-UTF16-V",
        "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H", "KSCms-UHC-HW-V",
        "KSCpc-EUC-H", "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V",
    };
}
