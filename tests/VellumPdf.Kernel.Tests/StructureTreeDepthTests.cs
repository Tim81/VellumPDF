// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.TestSupport;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// A deep structure tree saves. The walks that number the tree and write it are iterative, so the
/// depth of the tree is not bounded by the stack of the thread that calls the save. These tests run
/// the deep cases on a thread with a small stack, so a walk that recurses fails them by ending the
/// process rather than by a slow run, and assert the exact refusal for each input a save refuses.
/// </summary>
public sealed class StructureTreeDepthTests
{
    private const int Deep = 100_000;
    // The step-count test uses a smaller chain, so a mutant that walks each subtree (quadratic) still ends quickly.
    private const int Steps = 10_000;
    private const int SmallStackBytes = 256 * 1024;

    private static readonly DateTimeOffset Pinned = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    // ── Builders ─────────────────────────────────────────────────────────────

    /// <summary>A chain of <paramref name="n"/> grouping elements (L, LI, LBody, L, ...) ending in one <c>P</c> leaf.</summary>
    private static PdfStructElem Chain(int n, PdfPage page, string[]? types = null)
    {
        types ??= ["L", "LI", "LBody"];
        var root = new PdfStructElem(types[0]);
        var current = root;
        for (var i = 1; i < n; i++)
            current = current.AddChild(new PdfStructElem(types[i % types.Length]));
        current.AddChild(new PdfStructElem("P") { Page = page, Mcid = 0 });
        return root;
    }

    private static PdfDocument Tagged() => new() { Tagged = true, Timestamp = Pinned };

    private static PdfStructElem Leaf(PdfPage page, int mcid, string type = "P") => new(type) { Page = page, Mcid = mcid };

    /// <summary>The documents whose files are pinned by hash, keyed by shape.</summary>
    private static PdfDocument BuildShape(string shape)
    {
        var d = Tagged();
        switch (shape)
        {
            case "chain50":
                d.RegisterStructElem(Chain(50, d.AddPage()));
                break;
            case "chain3000":
                d.RegisterStructElem(Chain(3000, d.AddPage()));
                break;
            case "wide":
                {
                    var p = d.AddPage();
                    var r = new PdfStructElem("Div");
                    for (var i = 0; i < 200; i++)
                        r.AddChild(Leaf(p, i));
                    d.RegisterStructElem(r);
                    break;
                }
            case "twopages":
                {
                    var p1 = d.AddPage();
                    var p2 = d.AddPage();
                    var r = new PdfStructElem("Sect");
                    var a = new PdfStructElem("Div");
                    a.AddChild(Leaf(p2, 0));
                    a.AddChild(Leaf(p1, 0));
                    r.AddChild(a);
                    r.AddChild(Leaf(p1, 1, "H1"));
                    var g = new PdfStructElem("Div");
                    g.AddChild(new PdfStructElem("Figure") { Page = p2, Mcid = 3, AltText = "x" });
                    r.AddChild(g);
                    d.RegisterStructElem(r);
                    d.RegisterStructElem(Leaf(p1, 2));
                    break;
                }
            case "groupnopage":
                {
                    var p = d.AddPage();
                    var r = new PdfStructElem("Div");
                    var g = new PdfStructElem("Div");
                    r.AddChild(g);
                    g.AddChild(new PdfStructElem("Span"));
                    g.AddChild(Leaf(p, 0));
                    d.RegisterStructElem(r);
                    break;
                }
            // A grouping element whose first child holds no leaf and whose second does, and a second
            // grouping element that holds a leaf only through a nested one: the page of a grouping
            // element comes from the first descendant leaf in document order.
            case "firstleaf":
                {
                    var p1 = d.AddPage();
                    var p2 = d.AddPage();
                    var r = new PdfStructElem("Div");
                    var empty = r.AddChild(new PdfStructElem("Div"));
                    empty.AddChild(new PdfStructElem("Span"));
                    var g = r.AddChild(new PdfStructElem("Div"));
                    var inner = g.AddChild(new PdfStructElem("Sect"));
                    inner.AddChild(Leaf(p2, 0));
                    g.AddChild(Leaf(p1, 0));
                    d.RegisterStructElem(r);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
        return d;
    }

    private static byte[] Save(PdfDocument d)
    {
        using var ms = new MemoryStream();
        d.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Runs <paramref name="body"/> on a thread with a 256 KB stack and rethrows what it threw. The wait is a hang guard only.</summary>
    private static void OnSmallStack(Action body)
    {
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, SmallStackBytes)
        { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromMinutes(2)), "the save did not finish");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    // ── Byte identity with the recursive walks ───────────────────────────────

    // Each value was recorded by running these shapes on the commit before the walks became iterative.
    private static readonly Dictionary<string, string> ShapeHashes = new()
    {
        ["chain50"] = "573C2BE8332AD3BC76BC6AB69FB8152368E33A9549D5587E91CA46C1E0B621C8",
        ["chain3000"] = "54E2815F16BF6B47D06745AD356F0062B1C11E29D521469595480F3B329BE1E1",
        ["wide"] = "35E67ABE1D2AFFC761DA3F6607CF65F93DAE3616C8790E2E153D269010AC2A59",
        ["twopages"] = "9806F1C5D965B9038FE78FFD7D2C13A1809BCE7E4B987BD03D9AC3B6DF0FBB1A",
        ["groupnopage"] = "0CB14FF22E109872FC235918CE707AEC6C73C095BB23745E4E24CE8E54F1661C",
        ["firstleaf"] = "A6859F8736389D78BD4498128279A73FAA1C44FDFF4E42AC94C0E4AB239C3795",
    };

    public static TheoryData<string> Shapes => [.. ShapeHashes.Keys];

    /// <summary>The iterative walks number the elements, order the pages and derive each <c>/Pg</c> exactly as the recursive ones did.</summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void Save_ofAnOrdinaryTree_isByteIdenticalToTheRecursiveWalks(string shape)
    {
        var actual = CatalogGoldenTests.NormalisedSha256(Save(BuildShape(shape)));

        Assert.True(ShapeHashes[shape] == actual, $"{shape}: expected normalised sha256 {ShapeHashes[shape]}, computed {actual}.");
    }

    // ── Depth ────────────────────────────────────────────────────────────────

    private static readonly Regex StructElem = new(
        @"(?<n>\d+) 0 obj\n<<\n/Type /StructElem\n/S /(?<s>\w+)\n/P (?<p>\d+) 0 R",
        RegexOptions.CultureInvariant);

    /// <summary>The chain saved on a 256 KB stack holds every element once, and the leaf's <c>/P</c> links lead up to the structure tree root through all of them.</summary>
    [Fact]
    public void Save_ofAChainOf100000Elements_writesEveryElementAndTheWholeParentChain()
    {
        byte[] pdf = [];
        OnSmallStack(() =>
        {
            var d = Tagged();
            d.RegisterStructElem(Chain(Deep, d.AddPage()));
            pdf = Save(d);
        });

        var text = Encoding.Latin1.GetString(pdf);
        var elements = StructElem.Matches(text);
        // The chain, its leaf, and the writer's own /Document element.
        Assert.Equal(Deep + 2, elements.Count);
        Assert.Equal(Deep + 2, Regex.Matches(text, "/Type /StructElem", RegexOptions.CultureInvariant).Count);

        var parentOf = elements.ToDictionary(m => int.Parse(m.Groups["n"].Value), m => int.Parse(m.Groups["p"].Value));
        var root = int.Parse(Regex.Match(text, @"(\d+) 0 obj\n<<\n/Type /StructTreeRoot", RegexOptions.CultureInvariant).Groups[1].Value);
        var leaf = elements.Single(m => m.Groups["s"].Value == "P");

        var current = int.Parse(leaf.Groups["n"].Value);
        var hops = 0;
        while (current != root)
        {
            current = parentOf[current];
            hops++;
            Assert.True(hops <= Deep + 2, "the /P links do not reach the structure tree root");
        }
        Assert.Equal(Deep + 2, hops);
    }

    /// <summary>One <c>Save</c> of a deep tree on each path that writes the tree, on a 256 KB stack where the call is on the caller's thread.</summary>
    [Theory]
    [InlineData("objstm")]
    [InlineData("linearized")]
    [InlineData("encrypted")]
    [InlineData("signing")]
    [InlineData("async")]
    public async Task Save_ofAChainOf100000Elements_savesOnEveryPath(string path)
    {
        PdfDocument Make()
        {
            var d = Tagged();
            d.RegisterStructElem(Chain(Deep, d.AddPage()));
            return d;
        }

        byte[] pdf = [];
        string? password = null;
        switch (path)
        {
            case "objstm":
                OnSmallStack(() => { var d = Make(); d.UseObjectStreams = true; pdf = Save(d); });
                break;
            case "linearized":
                OnSmallStack(() => { var d = Make(); d.Linearize = true; pdf = Save(d); });
                break;
            case "encrypted":
                password = "u";
                OnSmallStack(() =>
                {
                    var d = Make();
                    d.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o" });
                    pdf = Save(d);
                });
                break;
            case "signing":
                OnSmallStack(() => pdf = Make().PrepareForSigning(new SignaturePlaceholderOptions { SigningTime = Pinned }));
                break;
            default:
                {
                    var d = Make();
                    using var ms = new MemoryStream();
                    await d.SaveAsync(ms, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
                    pdf = ms.ToArray();
                    break;
                }
        }

        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(pdf, 0, 8), StringComparison.Ordinal);
        var report = QpdfCheck(pdf, password);
        if (path == "linearized")
            Assert.Contains("File is linearized", report, StringComparison.Ordinal);
    }

    /// <summary>qpdf accepts the 100,000-deep file as written, and says so in its own words; the exit code alone would pass a warning too.</summary>
    [Fact]
    public void Save_ofAChainOf100000Elements_passesQpdfCheck()
    {
        byte[] pdf = [];
        OnSmallStack(() =>
        {
            var d = Tagged();
            d.RegisterStructElem(Chain(Deep, d.AddPage()));
            pdf = Save(d);
        });

        QpdfCheck(pdf, null);
    }

    private static string QpdfCheck(byte[] pdf, string? password)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vellum_struct_depth_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "deep.pdf");
            File.WriteAllBytes(file, pdf);
            List<string> args = password is null ? ["--check", file] : ["--password=" + password, "--check", file];
            ExternalTool.TryRun("qpdf", args, out var exit, out var stdout, out var stderr, out var timedOut, timeoutMs: 120_000);
            Assert.False(timedOut);
            Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
            Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);
            return stdout;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── The first-leaf pass costs one step per element and per edge ──────────

    private static readonly MethodInfo FirstLeafPages = typeof(PdfStructElem).Assembly
        .GetType("VellumPdf.Document.PdfStructureTree", throwOnError: true)!
        .GetMethod("FirstLeafPages", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

    private static Dictionary<PdfStructElem, PdfPage?> RunFirstLeafPages(PdfStructElem root, out int steps)
    {
        object?[] args = [root, 0];
        var map = (Dictionary<PdfStructElem, PdfPage?>)FirstLeafPages.Invoke(null, args)!;
        steps = (int)args[1]!;
        return map;
    }

    /// <summary>
    /// The page of each grouping element is found in one pass whose work is at most twice the number
    /// of elements (one step to enter an element, one per parent-child edge), not once per element over
    /// its whole subtree, which a chain would turn into a quadratic cost. Counted, not timed.
    /// </summary>
    [Fact]
    public void FirstLeafPages_ofAChainOf10000Elements_takesLinearSteps_andNamesTheLeafsPageForEveryGroup()
    {
        var d = Tagged();
        var page = d.AddPage();
        var root = Chain(Steps, page);
        var steps = 0;
        Dictionary<PdfStructElem, PdfPage?> map = [];

        OnSmallStack(() => map = RunFirstLeafPages(root, out steps));

        const int elements = Steps + 1; // the chain and its leaf
        Assert.True(steps <= 2 * elements, $"{steps} steps for {elements} elements");
        Assert.Equal(elements, map.Count);
        var current = root;
        for (var i = 0; i < Steps; i++)
        {
            Assert.Same(page, map[current]);
            current = current.Children[0];
        }
    }

    /// <summary>The first leaf below an element in document order names its page, whether it is a direct child or nested deeper, and an element reached by two routes is entered once.</summary>
    [Fact]
    public void FirstLeafPages_returnsTheFirstLeafInDocumentOrder()
    {
        var d = Tagged();
        var p1 = d.AddPage();
        var p2 = d.AddPage();
        var root = new PdfStructElem("Div");
        var empty = root.AddChild(new PdfStructElem("Div"));
        empty.AddChild(new PdfStructElem("Span"));
        var group = root.AddChild(new PdfStructElem("Div"));
        var inner = group.AddChild(new PdfStructElem("Sect"));
        var deepLeaf = inner.AddChild(Leaf(p2, 0));
        var sibling = group.AddChild(Leaf(p1, 1));
        var shared = new PdfStructElem("Div");
        shared.AddChild(Leaf(p1, 2));
        root.AddChild(shared);
        group.AddChild(shared);

        var map = RunFirstLeafPages(root, out var steps);

        Assert.Null(map[empty]);
        Assert.Same(p2, map[group]);
        Assert.Same(p2, map[inner]);
        Assert.Same(p2, map[root]);
        Assert.Same(p1, map[shared]);
        Assert.Null(map[deepLeaf]);
        Assert.Null(map[sibling]);
        Assert.True(steps <= 2 * map.Count + 2, $"{steps} steps for {map.Count} elements");
    }

    // ── What a save still refuses, with the same exception and message ───────

    private const string CycleMessage = "The structure tree contains a cycle: an element is its own ancestor, so the tree cannot be walked.";
    private const string NullMessage = "A structure element has a null child. (Parameter 'child')";

    private static PdfStructElem Cyclic(int n, PdfPage page)
    {
        var root = Chain(n, page);
        var last = root;
        while (last.Children.Count > 0)
            last = last.Children[0];
        last.AddChild(root);
        return root;
    }

    private static PdfDocument BuildRefusal(string shape)
    {
        var d = new PdfDocument { Tagged = true };
        var page = d.AddPage();
        switch (shape)
        {
            case "self-cycle":
                {
                    var e = new PdfStructElem("P");
                    e.AddChild(e);
                    d.RegisterStructElem(e);
                    break;
                }
            case "deep-cycle":
                d.RegisterStructElem(Cyclic(100, page));
                break;
            case "cycle-then-null":
                {
                    var r = new PdfStructElem("Div");
                    var c = r.AddChild(new PdfStructElem("Div"));
                    c.AddChild(c);
                    r.AddChild(null!);
                    d.RegisterStructElem(r);
                    break;
                }
            case "null-then-cycle":
                {
                    var r = new PdfStructElem("Div");
                    r.AddChild(null!);
                    var c = r.AddChild(new PdfStructElem("Div"));
                    c.AddChild(c);
                    d.RegisterStructElem(r);
                    break;
                }
            case "null-child":
                {
                    var e = new PdfStructElem("P");
                    e.AddChild(null!);
                    d.RegisterStructElem(e);
                    break;
                }
            case "null-deep":
                {
                    var r = Chain(1000, page);
                    var last = r;
                    while (last.Children.Count > 0 && last.Children[0].Children.Count > 0)
                        last = last.Children[0];
                    last.AddChild(null!);
                    d.RegisterStructElem(r);
                    break;
                }
            case "shared-no-leaf-4":
            case "shared-no-leaf-20":
                {
                    var r = new PdfStructElem("Div");
                    var current = r;
                    for (var i = 0; i < (shape.EndsWith("20", StringComparison.Ordinal) ? 20 : 4); i++)
                    {
                        var c = new PdfStructElem("Div");
                        current.AddChild(c);
                        current.AddChild(c);
                        current = c;
                    }
                    d.RegisterStructElem(r);
                    break;
                }
            case "shared-leaf":
                {
                    var leaf = Leaf(page, 0);
                    var a = new PdfStructElem("Div");
                    var b = new PdfStructElem("Div");
                    a.AddChild(leaf);
                    b.AddChild(leaf);
                    d.RegisterStructElem(a);
                    d.RegisterStructElem(b);
                    break;
                }
            case "diamond-with-leaf":
                {
                    var r = new PdfStructElem("Div");
                    var current = r;
                    for (var i = 0; i < 5; i++)
                    {
                        var c = new PdfStructElem("Div");
                        current.AddChild(c);
                        current.AddChild(c);
                        current = c;
                    }
                    current.AddChild(Leaf(page, 0));
                    d.RegisterStructElem(r);
                    break;
                }
            case "duplicate-mcid":
                {
                    var r = new PdfStructElem("Div");
                    r.AddChild(Leaf(page, 0));
                    r.AddChild(Leaf(page, 0));
                    d.RegisterStructElem(r);
                    break;
                }
            case "mcid-too-large":
                {
                    var r = new PdfStructElem("Div");
                    r.AddChild(Leaf(page, 1_000_001));
                    d.RegisterStructElem(r);
                    break;
                }
            case "non-standard-type":
                {
                    var r = new PdfStructElem("Div");
                    r.AddChild(new PdfStructElem("Foo"));
                    r.AddChild(new PdfStructElem("Bar") { Page = page, Mcid = 0 });
                    d.RegisterStructElem(r);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
        return d;
    }

    public static TheoryData<string, Type, string> Refusals => new()
    {
        { "self-cycle", typeof(InvalidOperationException), CycleMessage },
        { "deep-cycle", typeof(InvalidOperationException), CycleMessage },
        { "cycle-then-null", typeof(InvalidOperationException), CycleMessage },
        { "null-then-cycle", typeof(ArgumentNullException), NullMessage },
        { "null-child", typeof(ArgumentNullException), NullMessage },
        { "null-deep", typeof(ArgumentNullException), NullMessage },
        { "shared-no-leaf-4", typeof(InvalidOperationException), "Object 8 was reserved but never assigned a value." },
        { "shared-no-leaf-20", typeof(InvalidOperationException), "Object 8 was reserved but never assigned a value." },
        { "shared-leaf", typeof(InvalidOperationException), "Structure element on page 0 has duplicate MCID 0. Two structure elements claim the same MCID on the same page." },
        { "diamond-with-leaf", typeof(InvalidOperationException), "Structure element on page 0 has duplicate MCID 0. Two structure elements claim the same MCID on the same page." },
        { "duplicate-mcid", typeof(InvalidOperationException), "Structure element on page 0 has duplicate MCID 0. Two structure elements claim the same MCID on the same page." },
        { "mcid-too-large", typeof(InvalidOperationException), "Structure element on page 0 has MCID 1000001, which exceeds the maximum supported value (1000000). This indicates a malformed structure tree." },
        { "non-standard-type", typeof(InvalidOperationException), "Structure type 'Foo' is not a standard type and has no role mapping." },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Save_refusesWithTheSameExceptionAndMessage(string shape, Type type, string message)
    {
        var d = BuildRefusal(shape);

        var ex = Assert.Throws(type, () => d.Save(new MemoryStream()));

        Assert.Equal(message, ex.Message);
        if (ex is ArgumentNullException argument)
            Assert.Equal("child", argument.ParamName);
    }

    /// <summary>A cycle 100,000 elements long is refused by the cycle message, on a 256 KB stack; its walk would never end without the guard.</summary>
    [Fact]
    public void Save_ofACycleOf100000Elements_isRefusedOnASmallStack()
    {
        var d = new PdfDocument { Tagged = true };
        d.RegisterStructElem(Cyclic(Deep, d.AddPage()));

        OnSmallStack(() =>
        {
            var ex = Assert.Throws<InvalidOperationException>(() => d.Save(new MemoryStream()));
            Assert.Equal(CycleMessage, ex.Message);
        });
    }

    /// <summary>
    /// <c>Build</c> keeps nothing between saves: the first-leaf pass is local to a call, so a tree
    /// changed after one save is read afresh by the next document that holds the same elements.
    /// </summary>
    [Fact]
    public void Save_readsTheTreeAfresh_whenTheSameElementsAreSavedInASecondDocument()
    {
        var first = Tagged();
        var p1 = first.AddPage();
        var group = new PdfStructElem("Div");
        var holder = new PdfStructElem("Sect");
        holder.AddChild(group);
        first.RegisterStructElem(holder);
        first.RegisterStructElem(Leaf(p1, 0));
        var firstText = Encoding.Latin1.GetString(Save(first));

        var second = Tagged();
        var p2 = second.AddPage();
        group.AddChild(Leaf(p2, 0));
        second.RegisterStructElem(holder);
        var secondText = Encoding.Latin1.GetString(Save(second));

        // The group and the section holding it have no page of their own, so each gets /Pg only through
        // a leaf below it: the leaf alone in the first file, the leaf, the group and the section in the second.
        Assert.Single(Regex.Matches(firstText, @"/Pg \d+ 0 R"));
        Assert.Equal(3, Regex.Matches(secondText, @"/Pg \d+ 0 R").Count);
    }
}
