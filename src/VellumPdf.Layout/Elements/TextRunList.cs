// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// Validates and stores the runs a <see cref="Heading"/> or <see cref="ListItem"/> is built from.
/// A <see cref="Paragraph"/> keeps its own, laxer rule: it stores a run's null style because
/// 2.3.3 saved one, and these elements are new input.
/// </summary>
internal static class TextRunList
{
    /// <summary>
    /// Copies <paramref name="runs"/>, refusing a null run and a run whose style is null, and
    /// turns an empty sequence into one empty run in <paramref name="emptyStyle"/>.
    /// </summary>
    internal static List<TextRun> Copy(IEnumerable<TextRun> runs, string paramName, TextStyle emptyStyle)
    {
        var copy = new List<TextRun>(runs);
        for (var i = 0; i < copy.Count; i++)
        {
            if (copy[i] is null)
                throw new ArgumentNullException(paramName, $"The run at index {i} is null.");
            if (copy[i].Style is null)
                throw new ArgumentException($"The run at index {i} has a null style.", paramName);
        }

        if (copy.Count == 0)
            copy.Add(new TextRun(string.Empty, emptyStyle));
        return copy;
    }

    internal static string Concat(List<TextRun> runs) =>
        runs.Count == 1 ? runs[0].Text : string.Concat(runs.Select(r => r.Text));
}
