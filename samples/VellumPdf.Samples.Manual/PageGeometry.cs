// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Samples.Manual;

/// <summary>The page margins of the manual, in points.</summary>
public static class PageGeometry
{
    /// <summary>The margin on the binding side. With mirrored margins it is the left of a right-hand page.</summary>
    public const double Inner = 84;

    /// <summary>The margin on the outer edge.</summary>
    public const double Outer = 54;

    /// <summary>The margin above the running header.</summary>
    public const double Top = 76;

    /// <summary>The margin below the running footer.</summary>
    public const double Bottom = 76;
}
