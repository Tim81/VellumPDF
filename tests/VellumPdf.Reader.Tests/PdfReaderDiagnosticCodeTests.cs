// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Pins the two invariants <see cref="PdfReaderDiagnosticCode"/>'s own remarks document (#385): a
/// value never moves once shipped, and no later PR's block collides with an earlier one's. Every
/// member is mapped here to the hundred-wide area its name says it belongs to, so a reviewer adding
/// a code in a later PR checks this table rather than the enum's numbers by eye.
/// </summary>
public sealed class PdfReaderDiagnosticCodeTests
{
    // Member -> the area digit (1..9) its value must start with, matching the enum's own XML doc
    // block list. Extend this table in the same PR that appends a new PdfReaderDiagnosticCode
    // member — EveryMember_isMappedToAnArea fails loudly if one is forgotten.
    private static readonly Dictionary<PdfReaderDiagnosticCode, int> _area = new()
    {
        [PdfReaderDiagnosticCode.XrefReconstructed] = 1,
        [PdfReaderDiagnosticCode.OrphanedObjectStreamMembersDropped] = 1,
        [PdfReaderDiagnosticCode.ObjectStreamContainerUnreadable] = 1,
        [PdfReaderDiagnosticCode.ObjectHeaderMismatch] = 1,
        [PdfReaderDiagnosticCode.ObjectGenerationMismatch] = 1,
        [PdfReaderDiagnosticCode.FilterNull] = 1,
        [PdfReaderDiagnosticCode.FilterArrayElementNotName] = 1,
        [PdfReaderDiagnosticCode.FilterValueMalformed] = 1,
        [PdfReaderDiagnosticCode.DecodeParmsMalformed] = 1,
        [PdfReaderDiagnosticCode.UnsupportedPredictor] = 1,
        [PdfReaderDiagnosticCode.UnknownFilter] = 1,
        [PdfReaderDiagnosticCode.DecodedStreamLimitExceeded] = 1,
        [PdfReaderDiagnosticCode.DiagnosticsSuppressed] = 9,
    };

    private static PdfReaderDiagnosticCode[] AllCodes() =>
        (PdfReaderDiagnosticCode[])Enum.GetValues(typeof(PdfReaderDiagnosticCode));

    [Fact]
    public void EveryMember_isMappedToAnArea()
    {
        foreach (var code in AllCodes())
            Assert.True(_area.ContainsKey(code), $"{code} is missing from the area table above.");

        // Catches the opposite drift too: an area-table entry for a code that was renamed or
        // removed, which would otherwise let the loop above pass while the table silently rots.
        Assert.Equal(AllCodes().Length, _area.Count);
    }

    [Fact]
    public void EveryMember_valueFallsInsideItsDocumentedArea()
    {
        foreach (var (code, area) in _area)
        {
            var value = (int)code;
            var low = area * 100;
            var high = low + 99;
            Assert.True(
                value >= low && value <= high,
                $"{code} = {value} is outside the {area}xx range ({low}-{high}).");
        }
    }

    [Fact]
    public void NoValueIsReused()
    {
        var values = AllCodes().Select(c => (int)c).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    /// <summary>
    /// Calls <c>PdfReaderDiagnosticSeverities.Of</c> — the internal table
    /// <see cref="PdfReaderDiagnostic.Severity"/> is built from — for every currently defined code.
    /// A code missing its own arm there throws <c>UnreachableException</c> (its trailing arm), which
    /// this test turns into an immediate, CI-visible failure instead of a silent gap a caller only
    /// discovers when that specific condition is finally reported.
    /// </summary>
    [Fact]
    public void EveryCode_hasASeverity()
    {
        foreach (var code in AllCodes())
        {
            var diagnostic = MakeDiagnostic(code);
            Assert.True(
                Enum.IsDefined(typeof(PdfReaderDiagnosticSeverity), diagnostic.Severity),
                $"{code} reported an undefined severity value.");
        }
    }

    /// <summary>
    /// Builds a <see cref="PdfReaderDiagnostic"/> for <paramref name="code"/> through the same
    /// route production code uses — <see cref="DiagnosticSink.Report"/> — rather than calling the
    /// internal constructor directly, so this test exercises the actual severity lookup path.
    /// </summary>
    private static PdfReaderDiagnostic MakeDiagnostic(PdfReaderDiagnosticCode code)
    {
        var sink = new DiagnosticSink(cap: 10);
        sink.Report(code, "test");
        return Assert.Single(sink.Diagnostics);
    }

    // ── Exact value + severity pins ──────────────────────────────────────────────────────────────

    // The area/uniqueness tests above only bound a code to its 100-wide block; they would not
    // notice two codes swapping their exact values within that block (XrefReconstructed and
    // OrphanedObjectStreamMembersDropped trading 100/101, say). This table is the independently
    // written, intended value for each code, so a swap in the enum fails the theory below instead
    // of passing silently.
    private static readonly Dictionary<PdfReaderDiagnosticCode, (int Value, PdfReaderDiagnosticSeverity Severity)> _expected = new()
    {
        [PdfReaderDiagnosticCode.XrefReconstructed] = (100, PdfReaderDiagnosticSeverity.Info),
        [PdfReaderDiagnosticCode.OrphanedObjectStreamMembersDropped] = (101, PdfReaderDiagnosticSeverity.Warning),
        [PdfReaderDiagnosticCode.ObjectStreamContainerUnreadable] = (102, PdfReaderDiagnosticSeverity.Warning),
        [PdfReaderDiagnosticCode.ObjectHeaderMismatch] = (103, PdfReaderDiagnosticSeverity.Warning),
        [PdfReaderDiagnosticCode.ObjectGenerationMismatch] = (104, PdfReaderDiagnosticSeverity.Warning),
        [PdfReaderDiagnosticCode.FilterNull] = (105, PdfReaderDiagnosticSeverity.Info),
        [PdfReaderDiagnosticCode.FilterArrayElementNotName] = (106, PdfReaderDiagnosticSeverity.Warning),
        [PdfReaderDiagnosticCode.FilterValueMalformed] = (107, PdfReaderDiagnosticSeverity.Warning),
        [PdfReaderDiagnosticCode.DecodeParmsMalformed] = (108, PdfReaderDiagnosticSeverity.Warning),
        [PdfReaderDiagnosticCode.UnsupportedPredictor] = (109, PdfReaderDiagnosticSeverity.Warning),
        [PdfReaderDiagnosticCode.UnknownFilter] = (110, PdfReaderDiagnosticSeverity.Error),
        [PdfReaderDiagnosticCode.DecodedStreamLimitExceeded] = (111, PdfReaderDiagnosticSeverity.Error),
        [PdfReaderDiagnosticCode.DiagnosticsSuppressed] = (900, PdfReaderDiagnosticSeverity.Warning),
    };

    public static IEnumerable<object[]> ExpectedValueAndSeverityCases() =>
        _expected.Select(kv => new object[] { kv.Key, kv.Value.Value, kv.Value.Severity });

    [Theory]
    [MemberData(nameof(ExpectedValueAndSeverityCases))]
    public void Code_hasThePinnedValueAndSeverity(
        PdfReaderDiagnosticCode code, int expectedValue, PdfReaderDiagnosticSeverity expectedSeverity)
    {
        Assert.Equal(expectedValue, (int)code);

        var diagnostic = MakeDiagnostic(code);
        Assert.Equal(expectedSeverity, diagnostic.Severity);
    }

    [Fact]
    public void ExpectedValueAndSeverityCases_coverEveryCode()
    {
        // Catches a code added without a matching row above — the theory itself only proves the
        // rows it has are correct, not that every current member has one.
        Assert.Equal(AllCodes().Length, _expected.Count);
    }
}
