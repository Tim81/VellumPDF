// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// #610: the reconstruction walk parses through <see cref="PdfObjectParser.CreateForScan"/>, which
/// reports a malformed construct by return value instead of by an exception unwound through every
/// nesting level. The walk's outcomes (what it recovers, when its budget refuses) depend on the
/// lexer position a failed parse leaves behind and on which failures count as failures at all, so
/// the scan parser must agree with the throwing parser on both, input by input. These tests are
/// that agreement, value-level: same outcome category, same <see cref="PdfObjectParser.Position"/>,
/// same bytes for a parsed value.
/// </summary>
public sealed class ScanModeParserTests
{
    private static readonly string[] _atoms =
    [
        "[", "[", "[", "]", "]", "<<", "<<", ">>", ">>", "/A", "/B", "/#41", "/#G1", "/#4", "/#", "/",
        "1", "-7", "+3", ".5", "1.5", "--1", "99999999999999999999", "4294967297",
        new string('9', 400) + ".5", new string('9', 400),
        "1 0 R", "12 7 R", "4294967297 0 R", "1 99999999999 R", "1 0 obj", "obj", "stream", "endobj",
        "(abc)", "(a(b)c)", "(unterminated", "(a\\", "(\\", "<4142>", "<41 42>", "<GG>", "<41", "<4>",
        "true", "false", "null", "foo", "R", ">", "<", "{", "}", ")", "%c\n", "%unterminated",
        "\0", "ÿ", "\f",
    ];

    private static readonly string[] _separators = [" ", "", "", "\n", "\r\n", "\t", "%x\n"];

    private static string Build(Random rng)
    {
        var sb = new StringBuilder();
        // A nesting prefix of the lengths that straddle MaxNestingDepth (256), so the depth refusal
        // is exercised with and without a matching close behind it.
        switch (rng.Next(8))
        {
            case 0: sb.Append('[', rng.Next(250, 262)); break;
            case 1: sb.Insert(0, string.Concat(Enumerable.Repeat("<</A", rng.Next(250, 262)))); break;
            case 2: sb.Insert(0, string.Concat(Enumerable.Repeat("[1", rng.Next(250, 262)))); break;
            case 3: sb.Insert(0, string.Concat(Enumerable.Repeat("<</A[", rng.Next(120, 135)))); break;
        }

        var count = rng.Next(1, 14);
        for (var i = 0; i < count; i++)
        {
            sb.Append(_atoms[rng.Next(_atoms.Length)]);
            sb.Append(_separators[rng.Next(_separators.Length)]);
        }

        if (rng.Next(6) == 0)
            sb.Append(']', rng.Next(1, 300));
        if (rng.Next(6) == 0)
            sb.Append(string.Concat(Enumerable.Repeat(">>", rng.Next(1, 150))));
        return sb.ToString();
    }

    private static (string Outcome, int Position, string Value) RunThrowing(byte[] data, int offset)
    {
        var parser = new PdfObjectParser(data, offset);
        try
        {
            var v = parser.ParseObject();
            return ("ok", parser.Position, Convert.ToHexString(XrefReconstructionTests.WriteToBytes(v)));
        }
        catch (InvalidDataException)
        {
            return ("fail", parser.Position, "");
        }
        catch (Exception e)
        {
            return (e.GetType().FullName!, parser.Position, "");
        }
    }

    private static (string Outcome, int Position, string Value) RunScan(byte[] data, int offset)
    {
        var parser = PdfObjectParser.CreateForScan(data, offset);
        try
        {
            return parser.TryParseObjectScan(out var v)
                ? ("ok", parser.Position, Convert.ToHexString(XrefReconstructionTests.WriteToBytes(v!)))
                : ("fail", parser.Position, "");
        }
        catch (Exception e)
        {
            return (e.GetType().FullName!, parser.Position, "");
        }
    }

    [Fact]
    public void ScanParse_AgreesWithThrowingParse_OnSeededRandomInputs()
    {
        var rng = new Random(610);
        var ok = 0;
        var fail = 0;
        for (var i = 0; i < 30_000; i++)
        {
            var text = Build(rng);
            var data = Encoding.Latin1.GetBytes(text);
            var offset = rng.Next(0, Math.Min(3, data.Length + 1));
            var expected = RunThrowing(data, offset);
            var actual = RunScan(data, offset);
            Assert.True(
                expected == actual,
                $"input #{i} at offset {offset}: throwing parser gave {expected}, scan parser gave {actual}. "
                + $"Input: {text[..Math.Min(text.Length, 200)]}");
            if (expected.Outcome == "ok") ok++; else fail++;
        }

        // The generator must reach both sides, or agreement proves nothing.
        Assert.True(ok > 1_000, $"only {ok} generated inputs parsed.");
        Assert.True(fail > 1_000, $"only {fail} generated inputs failed.");
    }

    // One input per failure kind named in the #610 review. Each is asserted to fail (or succeed) in
    // the throwing parser first, so the case cannot pass by both sides doing something unexpected.
    public static TheoryData<string, string> NamedKinds => new()
    {
        { "number overflow", "[1 99999999999999999999 2]" },
        { "number overflow bare", "99999999999999999999" },
        { "real out of range", "[" + new string('9', 400) + ".5]" },
        { "bad hex string", "[<4G>]" },
        { "unterminated hex string", "[<4142" },
        { "bad name escape", "<</A#G1 2>>" },
        { "empty name", "[/ 1]" },
        { "EOF inside string", "[(abc" },
        { "EOF inside nested string", "<</A (a(b)c" },
        { "EOF inside array", "[1 2 3" },
        { "EOF inside dictionary", "<</A 1 /B" },
        { "non-name dictionary key", "<<1 2>>" },
        { "stray keyword", "[foo]" },
        { "stray close", "]" },
        { "stray dict close", "[>>]" },
        { "out-of-range reference object", "[4294967297 0 R]" },
        { "valid nested", "[1 [2 <</A [3]>>] (s) <41> /N 1 0 R null true]" },
        { "depth 256 closes", "[[[[" },
    };

    [Theory]
    [MemberData(nameof(NamedKinds))]
    public void ScanParse_AgreesWithThrowingParse_OnEachNamedFailureKind(string kind, string text)
    {
        var data = Encoding.Latin1.GetBytes(text);
        Assert.Equal(RunThrowing(data, 0), RunScan(data, 0));
        Assert.NotEmpty(kind);
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    [InlineData(300, false)]
    public void ScanParse_RefusesNestingBeyondTheDepthCap_AtTheSamePosition(int depth, bool closes)
    {
        foreach (var data in new[]
        {
            Encoding.ASCII.GetBytes(new string('[', depth) + new string(']', depth)),
            Encoding.ASCII.GetBytes(new string('[', depth)),
            Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("<</A", depth)) + "1" + new string('>', depth * 2)),
            Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("[1 ", depth))),
        })
        {
            var expected = RunThrowing(data, 0);
            Assert.Equal(expected, RunScan(data, 0));
            // Position 257 is the 257th '[' consumed: pins the depth cap itself, not just agreement.
            if (data[0] == (byte)'[' && data[^1] == (byte)']')
                Assert.Equal(closes ? "ok" : "fail", expected.Outcome);
        }
    }

    // ProbeIndirectObjectHeader has its own scan branch (the reconstruction walk's two header
    // probes). Its caller catches InvalidDataException and reads Position, so "fail" here is the
    // exception, which the scan branch raises at the top of the probe instead of from nesting depth.
    private static (string Outcome, int Position, string Value) Probe(byte[] data, bool scan)
    {
        var parser = scan ? PdfObjectParser.CreateForScan(data, 0) : new PdfObjectParser(data, 0);
        try
        {
            var r = parser.ProbeIndirectObjectHeader();
            var value = r.Value is null ? "" : Convert.ToHexString(XrefReconstructionTests.WriteToBytes(r.Value));
            return ($"ok {r.ObjectNumber} {r.Generation} {r.HasStreamBody} {r.StreamBodyStart}", parser.Position, value);
        }
        catch (InvalidDataException)
        {
            return ("fail", parser.Position, "");
        }
        catch (Exception e)
        {
            return (e.GetType().FullName!, parser.Position, "");
        }
    }

    [Fact]
    public void ProbeIndirectObjectHeader_ScanBranch_AgreesWithThrowingBranch()
    {
        var rng = new Random(6100);
        string[] bodies =
        [
            "<</A 1>> stream\n", "<</A 1>> stream", "<</A 1>> endobj", "<</A 1>>", "<</A [1 2]>> endobj", "[1 2] endobj",
            "[1 2]", "[1 [2 ] endobj", "42 endobj", "(s) endobj", "<4G> endobj", "<</A <4G>>> endobj",
            "99999999999999999999 endobj", "<</A 99999999999999999999>> stream\r\n", "<<1 2>> endobj",
            "<</A (abc", "stream", "foo endobj", "[" + new string('[', 300) + " endobj",
            "<</A" + string.Concat(Enumerable.Repeat("<</A", 300)), "[1 " + string.Concat(Enumerable.Repeat("[1 ", 300)),
        ];
        string[] heads = ["1 0 obj ", "12 3 obj\n", "1 0 obj", "4294967297 0 obj ", "1 0 foo ", "1 x obj ", "1 0 obj%c\n"];
        foreach (var head in heads)
        {
            foreach (var body in bodies)
            {
                var data = Encoding.Latin1.GetBytes(head + body);
                Assert.Equal(Probe(data, scan: false), Probe(data, scan: true));
            }
        }

        for (var i = 0; i < 5_000; i++)
        {
            var data = Encoding.Latin1.GetBytes(heads[rng.Next(2)] + Build(rng));
            Assert.Equal(Probe(data, scan: false), Probe(data, scan: true));
        }
    }

    [Fact]
    public void ThrowingParser_KeepsItsExceptionTypesAndMessages()
    {
        // The throwing parser is not touched by #610 (scan mode is separate code). Pin the messages
        // and types a few callers see so a later edit that routes it through scan mode shows up.
        static string Message(string text) =>
            Assert.Throws<InvalidDataException>(() => new PdfObjectParser(Encoding.Latin1.GetBytes(text)).ParseObject()).Message;

        Assert.Equal("Unterminated array; missing ']'.", Message("[1 2"));
        Assert.Equal("Unterminated dictionary; missing '>>'.", Message("<</A 1"));
        Assert.StartsWith("PDF object nesting exceeds 256 levels", Message(new string('[', 300)));
        Assert.StartsWith("Expected a name key in dictionary, got Integer", Message("<<1 2>>"));
        Assert.StartsWith("Malformed integer:", Message("99999999999999999999"));
        Assert.Equal("Unexpected end of input; expected a PDF object.", Message(""));
    }

    [Fact]
    public void ScanParse_ParsedValueEqualsThrowingParseValue_ForTheEmptyContainers()
    {
        foreach (var text in new[] { "[]", "<<>>", "[[]]", "<</A []>>", "<</A <<>>>>", "[ % c\n ]" })
        {
            var data = Encoding.ASCII.GetBytes(text);
            var expected = RunThrowing(data, 0);
            Assert.Equal("ok", expected.Outcome);
            Assert.Equal(expected, RunScan(data, 0));
        }
    }
}
