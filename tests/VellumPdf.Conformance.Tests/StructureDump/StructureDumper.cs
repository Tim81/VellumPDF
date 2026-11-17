// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Reader;
using VellumPdf.Reader.Content;

namespace VellumPdf.Conformance.Tests.StructureDump;

/// <summary>
/// Prints the logical structure of a PDF next to the marked content it describes, as text, so a
/// test can pin "what the writer produced" as a golden file and a later fix shows up as a diff.
/// It reads the structure tree (ISO 32000-2 §14.7), the parent tree (§14.7.5.4), each page's content
/// stream (§14.6) and each page's annotations (§12.5), and states where they disagree.
/// </summary>
/// <remarks>
/// <b>It does not use the production structure reader.</b> <c>StructureTree</c> collapses MCID, MCR
/// and OBJR kids into a flag, de-duplicates elements by object number, truncates silently at its own
/// caps and drops a repeated parent-tree key; each of those hides exactly what a golden has to show.
/// This type walks <c>/K</c> and <c>/ParentTree</c> itself, and prints a problem line
/// (<c>DUPLICATE-ELEMENT</c>, <c>UNRESOLVED</c>, <c>CYCLE</c>, <c>DUP-KEY</c>) where it would
/// otherwise skip.
/// <para>
/// <b>A cap is refused, not truncated.</b> <see cref="Dump"/> throws
/// <see cref="InvalidOperationException"/> naming the cap when the element depth exceeds
/// <see cref="MaxElementDepth"/>, the number of structure nodes exceeds <see cref="MaxNodes"/>
/// (elements and kids together, plus parent-tree entries), the parent tree nests deeper than
/// <see cref="MaxParentTreeDepth"/>, or marked content nests deeper than
/// <see cref="MaxMarkedContentDepth"/> (counted across a page and every form it draws; the Reader's
/// own cap is not a bound for a visitor, because the Reader forwards the operators it ignores).
/// The printed length of one value, array or string is capped separately and says so with
/// <c>…(+N)</c>. The output size is otherwise proportional to the content: the only bound on a
/// page's content is the Reader's own decoded-bytes budget. A document the Reader cannot open throws
/// whatever <c>PdfReader.Open</c> throws, usually <see cref="InvalidDataException"/>.
/// </para>
/// <para>
/// <b>Text is decoded through <c>/ToUnicode</c> only.</b> A show operand prints as Unicode when the
/// font has a <c>/ToUnicode</c> CMap and is either a simple font (one-byte codes) or a Type0 font
/// whose <c>/Encoding</c> is <c>Identity-H</c> or <c>Identity-V</c> (two-byte codes). Any other font
/// (no <c>/ToUnicode</c>, or a Type0 font with another encoding CMap) prints its raw codes as
/// <c>show &lt;hex&gt;</c>. A code the CMap does not cover prints as <c>\u{FFFD}</c>. Nothing here
/// substitutes <c>/ActualText</c>, interprets TJ adjustments as spaces, or applies a font's own
/// encoding. A text string (<c>/Alt</c>, <c>/T</c>) is read as UTF-16BE when it starts with a byte
/// order mark FE FF, as UTF-8 after EF BB BF, and as Latin-1 otherwise, which differs from
/// PDFDocEncoding in 0x18-0x1F and 0x80-0x9E.
/// </para>
/// <para>
/// <b>What is not printed.</b> Object numbers never appear (a form is <c>form{n}</c>, numbered per
/// page in order of first invocation; an element is its index path). Numbers print with the
/// invariant culture, <c>"0.##"</c>, and <c>-0</c> prints as <c>0</c>. Every key of a structure
/// element except <c>/K /P /Pg /S /Type</c> is printed. <c>/Pg</c> is read from the element or the
/// MCR/OBJR itself and is not inherited from an ancestor, because that is what the writer emits.
/// A form XObject's marked-content identifiers belong to that form and its <c>/StructParents</c>, not
/// to the page; a form drawn on two pages counts once, and is labelled by the page that drew it
/// first. <b>Do not pass an encrypted document expecting readable text.</b> The Reader decrypts
/// what it can, and a failure to authenticate surfaces as the Reader's own exception rather than
/// as a dump.
/// </para>
/// </remarks>
internal static class StructureDumper
{
    /// <summary>Deepest structure element nesting <see cref="Dump"/> accepts.</summary>
    internal const int MaxElementDepth = 256;

    /// <summary>Most structure nodes (elements and kids) plus parent-tree entries <see cref="Dump"/> accepts.</summary>
    internal const int MaxNodes = 100_000;

    /// <summary>Deepest number-tree nesting <see cref="Dump"/> accepts in <c>/ParentTree</c>.</summary>
    internal const int MaxParentTreeDepth = 64;

    /// <summary>Deepest <c>BMC</c>/<c>BDC</c> nesting <see cref="Dump"/> accepts, summed over a page and its forms.</summary>
    internal const int MaxMarkedContentDepth = 64;

    /// <summary>
    /// Dumps <paramref name="pdf"/>. <paramref name="knownDefects"/> is printed as the first line when
    /// non-empty, so a golden reads as "wrong on purpose until these land".
    /// </summary>
    /// <exception cref="InvalidOperationException">A cap named in the type remarks was exceeded.</exception>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a document the Reader opens.</exception>
    internal static string Dump(byte[] pdf, IReadOnlyList<string>? knownDefects = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var reader = PdfReader.Open(pdf);
        return new Session(reader).Run(knownDefects);
    }

    private readonly record struct StreamKey(int Page, int FormObj);

    private enum KidKind { Mcid, Mcr, Objr, Elem, Problem }

    private sealed class Kid
    {
        internal KidKind Kind;
        internal int Mcid;
        internal int Page = -1;
        internal int StmObj = -1;
        internal Elem? Child;
        internal string Text = "";
        internal PdfDictionary? Obj;
        internal string Subtype = "";
    }

    private sealed class Elem
    {
        internal required PdfDictionary Dict;
        internal string Type = "?";
        internal string Display = "?";
        internal string Path = "";
        internal int Page = -1;
        internal readonly List<Kid> Kids = [];
    }

    private sealed class StreamInfo
    {
        internal required StreamKey Key;
        internal required string Label;
        internal readonly List<int> Mcids = [];
        internal PdfDictionary? FormDict;
    }

    private sealed class PageSection
    {
        internal readonly List<string> Lines = [];
        internal readonly Dictionary<int, int> Forms = [];
    }

    private sealed class Session(PdfDocumentReader reader)
    {
        private static readonly PdfName KeyK = new("K");
        private static readonly PdfName KeyPg = new("Pg");
        private static readonly PdfName KeyS = new("S");
        private static readonly PdfName KeyP = new("P");
        private static readonly PdfName KeyMcid = new("MCID");
        private static readonly PdfName KeyStm = new("Stm");
        private static readonly PdfName KeyObj = new("Obj");

        private readonly PdfDocumentReader _reader = reader;
        private readonly Dictionary<int, int> _pageByObj = [];
        private readonly Dictionary<PdfDictionary, Elem> _elemByDict = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<PdfDictionary> _onStack = new(ReferenceEqualityComparer.Instance);
        private readonly List<(StreamKey Key, int Mcid, Elem Owner)> _structMcids = [];
        private readonly List<(Elem Owner, PdfDictionary? Annot, int Page)> _objrs = [];
        private readonly List<PageSection> _sections = [];
        private readonly List<StreamInfo> _streams = [];
        private readonly Dictionary<StreamKey, StreamInfo> _streamByKey = [];
        private readonly Dictionary<int, string> _formLabels = [];
        private readonly List<(int Key, PdfObject Value)> _tree = [];
        private readonly Dictionary<int, PdfObject> _treeByKey = [];
        private readonly List<string> _problems = [];
        private readonly List<string> _treeProblems = [];
        private PdfDictionary? _roleMap;
        private PdfDictionary? _structRoot;
        private Elem? _root;
        private int _nodes;

        internal string Run(IReadOnlyList<string>? knownDefects)
        {
            var pages = _reader.Pages;
            for (var i = 0; i < pages.Count; i++)
            {
                if (pages[i].ObjectNumber is { } n)
                    _pageByObj.TryAdd(n, i + 1);
            }

            _structRoot = Res(_reader.Catalog.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
            _roleMap = _structRoot is null ? null : Res(_structRoot.Get(new PdfName("RoleMap"))) as PdfDictionary;

            // Content first: the structure listing labels an MCR's /Stm with the form's per-page
            // ordinal, which only the content pass knows.
            for (var i = 0; i < pages.Count; i++)
                _sections.Add(RunPage(pages[i], i + 1));

            if (_structRoot is not null)
            {
                _root = new Elem { Dict = _structRoot, Type = "StructTreeRoot", Display = "StructTreeRoot" };
                WalkKids(_root, _structRoot.Get(KeyK), 1);
                AssignPaths(_root);
                ReadParentTree();
            }

            var annotLines = new List<List<string>>();
            var annots = new List<(int Page, int Index, PdfDictionary Dict)>();
            for (var i = 0; i < pages.Count; i++)
                annotLines.Add(DumpAnnotations(pages[i], i + 1, annots));

            RunChecks(annots);

            var sb = new StringBuilder();
            void L(string s) => sb.Append(s).Append('\n');

            if (knownDefects is { Count: > 0 })
                L("known defects: " + string.Join(", ", knownDefects));
            WriteHeader(L, pages.Count);

            L("== structure");
            if (_root is null)
            {
                L("(no /StructTreeRoot)");
            }
            else
            {
                L("StructTreeRoot");
                PrintKids(_root, 1, L);
            }

            for (var i = 0; i < pages.Count; i++)
            {
                L($"== page {Inv(i + 1)} content");
                foreach (var line in _sections[i].Lines)
                    L(line);
                L($"== page {Inv(i + 1)} annotations");
                if (annotLines[i].Count == 0)
                    L("(none)");
                foreach (var line in annotLines[i])
                    L(line);
            }

            L("== checks");
            foreach (var p in _treeProblems.Concat(_problems))
                L(p);
            L($"{Inv(_treeProblems.Count + _problems.Count)} problem line(s)");
            return sb.ToString();
        }

        // ── Header ─────────────────────────────────────────────────────────────────────────────

        private void WriteHeader(Action<string> l, int pageCount)
        {
            var cat = _reader.Catalog;
            l("Lang: " + (Res(cat.Get(new PdfName("Lang"))) is { } lang ? Fmt(lang, 0) : "absent"));
            l("MarkInfo: " + (Res(cat.Get(new PdfName("MarkInfo"))) is { } mi ? Fmt(mi, 0) : "absent"));
            var vp = Res(cat.Get(new PdfName("ViewerPreferences"))) as PdfDictionary;
            l("DisplayDocTitle: " + (vp is not null && Res(vp.Get(new PdfName("DisplayDocTitle"))) is { } dd ? Fmt(dd, 0) : "absent"));
            l("Pages: " + Inv(pageCount));
            if (_structRoot is null)
            {
                l("ParentTreeNextKey: n/a");
                return;
            }

            if (Res(_structRoot.Get(new PdfName("ParentTreeNextKey"))) is PdfInteger next)
            {
                var exceeds = _tree.Count == 0 || _tree.All(t => next.Value > t.Key);
                l($"ParentTreeNextKey: {Inv(next.Value)} (exceeds every key: {(exceeds ? "yes" : "no")})");
            }
            else
            {
                l("ParentTreeNextKey: absent");
            }
        }

        // ── Structure tree ─────────────────────────────────────────────────────────────────────

        private void WalkKids(Elem owner, PdfObject? k, int depth)
        {
            if (k is null)
                return;

            var items = new List<PdfObject>();
            var resolved = Res(k);
            if (resolved is PdfArray arr)
            {
                for (var i = 0; i < arr.Count; i++)
                    items.Add(arr[i]);
            }
            else
            {
                items.Add(k);
            }

            foreach (var raw in items)
            {
                if (++_nodes > MaxNodes)
                    throw new InvalidOperationException(
                        $"Structure node cap exceeded: more than {Inv(MaxNodes)} structure nodes.");

                if (raw is PdfInteger mc)
                {
                    owner.Kids.Add(new Kid { Kind = KidKind.Mcid, Mcid = (int)mc.Value, Page = owner.Page });
                    RecordMcid(owner, owner.Page, -1, (int)mc.Value);
                    continue;
                }

                if (Res(raw) is not PdfDictionary d)
                {
                    owner.Kids.Add(new Kid { Kind = KidKind.Problem, Text = "UNRESOLVED" });
                    continue;
                }

                var type = Res(d.Get(PdfName.Type)) is PdfName tn ? tn.Value : "";
                if (type == "MCR")
                {
                    var mcid = Res(d.Get(KeyMcid)) is PdfInteger mi ? (int)mi.Value : -1;
                    var page = d.Get(KeyPg) is { } pg ? PageOf(pg) : owner.Page;
                    var stm = d.Get(KeyStm) is PdfIndirectReference sr ? sr.ObjectNumber : -1;
                    owner.Kids.Add(new Kid { Kind = KidKind.Mcr, Mcid = mcid, Page = page, StmObj = stm });
                    RecordMcid(owner, page, stm, mcid);
                }
                else if (type == "OBJR")
                {
                    var page = d.Get(KeyPg) is { } pg ? PageOf(pg) : owner.Page;
                    var target = Res(d.Get(KeyObj)) as PdfDictionary;
                    var sub = target is not null && Res(target.Get(PdfName.Subtype)) is PdfName sn ? sn.Value : "?";
                    owner.Kids.Add(new Kid { Kind = KidKind.Objr, Page = page, Obj = target, Subtype = sub });
                    _objrs.Add((owner, target, page));
                }
                else if (_onStack.Contains(d))
                {
                    owner.Kids.Add(new Kid { Kind = KidKind.Problem, Text = "CYCLE" });
                }
                else if (_elemByDict.ContainsKey(d))
                {
                    owner.Kids.Add(new Kid { Kind = KidKind.Problem, Text = "DUPLICATE-ELEMENT" });
                }
                else
                {
                    if (depth > MaxElementDepth)
                        throw new InvalidOperationException(
                            $"Structure element depth cap exceeded: nesting deeper than {Inv(MaxElementDepth)}.");

                    var s = Res(d.Get(KeyS)) is PdfName sName ? sName.Value : "?";
                    var elem = new Elem
                    {
                        Dict = d,
                        Type = s,
                        Display = DisplayType(s),
                        Page = d.Get(KeyPg) is { } ep ? PageOf(ep) : -1,
                    };
                    _elemByDict[d] = elem;
                    owner.Kids.Add(new Kid { Kind = KidKind.Elem, Child = elem });
                    _onStack.Add(d);
                    WalkKids(elem, d.Get(KeyK), depth + 1);
                    _onStack.Remove(d);
                }
            }
        }

        private void RecordMcid(Elem owner, int page, int stmObj, int mcid) =>
            _structMcids.Add((stmObj >= 0 ? new StreamKey(-1, stmObj) : new StreamKey(page, -1), mcid, owner));

        private int PageOf(PdfObject pg) =>
            pg is PdfIndirectReference r && _pageByObj.TryGetValue(r.ObjectNumber, out var n) ? n : -1;

        private string DisplayType(string s)
        {
            if (StructureTree.StandardTypes.Contains(s))
                return s;

            var cur = s;
            var seen = new HashSet<string>(StringComparer.Ordinal) { s };
            for (var i = 0; i < 32; i++)
            {
                if (_roleMap is null || Res(_roleMap.Get(new PdfName(cur))) is not PdfName next)
                    return s + "→?";
                if (StructureTree.StandardTypes.Contains(next.Value))
                    return s + "→" + next.Value;
                if (!seen.Add(next.Value))
                    return s + "→?";
                cur = next.Value;
            }

            return s + "→?";
        }

        // The index is among a parent's element kids only; a single top-level element is printed
        // bare so the path reads "Document/L[0]/LI[1]".
        private static void AssignPaths(Elem root)
        {
            var top = root.Kids.Where(k => k.Kind == KidKind.Elem).Select(k => k.Child!).ToList();
            for (var i = 0; i < top.Count; i++)
            {
                top[i].Path = top.Count == 1 ? top[i].Type : top[i].Type + "[" + Inv(i) + "]";
                AssignChildPaths(top[i]);
            }
        }

        private static void AssignChildPaths(Elem e)
        {
            var i = 0;
            foreach (var kid in e.Kids.Where(k => k.Kind == KidKind.Elem))
            {
                kid.Child!.Path = e.Path + "/" + kid.Child.Type + "[" + Inv(i++) + "]";
                AssignChildPaths(kid.Child);
            }
        }

        private void PrintKids(Elem e, int indent, Action<string> l)
        {
            var pad = new string(' ', indent * 2);
            foreach (var kid in e.Kids)
            {
                switch (kid.Kind)
                {
                    case KidKind.Mcid:
                        l($"{pad}mcid {Inv(kid.Mcid)} {PageText(kid.Page)}");
                        if (kid.Page < 0)
                            l(pad + "NO-PG");
                        break;
                    case KidKind.Mcr:
                        var stm = kid.StmObj >= 0 ? " stm " + FormLabelOnPage(kid.StmObj, kid.Page) : "";
                        l($"{pad}MCR mcid {Inv(kid.Mcid)} {PageText(kid.Page)}{stm}");
                        if (kid.Page < 0)
                            l(pad + "NO-PG");
                        break;
                    case KidKind.Objr:
                        l($"{pad}OBJR {kid.Subtype} {PageText(kid.Page)}");
                        if (kid.Page < 0)
                            l(pad + "NO-PG");
                        break;
                    case KidKind.Problem:
                        l(pad + kid.Text);
                        break;
                    case KidKind.Elem:
                        l(pad + ElementLine(kid.Child!));
                        PrintKids(kid.Child!, indent + 1, l);
                        break;
                }
            }
        }

        private static string PageText(int page) => page > 0 ? "p" + Inv(page) : "p?";

        private string FormLabelOnPage(int formObj, int page)
        {
            if (page > 0 && page <= _sections.Count && _sections[page - 1].Forms.TryGetValue(formObj, out var n))
                return "form" + Inv(n);
            return "form?";
        }

        private string ElementLine(Elem e)
        {
            var sb = new StringBuilder(e.Display);
            var entries = Entries(e.Dict)
                .Where(kv => kv.Key.Value is not ("K" or "P" or "Pg" or "S" or "Type"))
                .OrderBy(kv => kv.Key.Value, StringComparer.Ordinal);
            foreach (var kv in entries)
            {
                sb.Append(' ').Append(kv.Key.Value).Append('=');
                sb.Append(kv.Key.Value is "A" or "C" ? FmtAttrList(kv.Value) : Fmt(kv.Value, 0));
            }

            return sb.ToString();
        }

        // ── ParentTree ─────────────────────────────────────────────────────────────────────────

        private void ReadParentTree()
        {
            if (_structRoot is null || Res(_structRoot.Get(new PdfName("ParentTree"))) is not PdfDictionary pt)
                return;

            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            WalkNumberTree(pt, 1, seen);
        }

        private void WalkNumberTree(PdfDictionary node, int depth, HashSet<PdfDictionary> seen)
        {
            if (depth > MaxParentTreeDepth)
                throw new InvalidOperationException(
                    $"ParentTree depth cap exceeded: nesting deeper than {Inv(MaxParentTreeDepth)}.");
            if (!seen.Add(node))
            {
                _treeProblems.Add("PARENTTREE CYCLE");
                return;
            }

            if (Res(node.Get(new PdfName("Nums"))) is PdfArray nums)
            {
                for (var i = 0; i + 1 < nums.Count; i += 2)
                {
                    if (++_nodes > MaxNodes)
                        throw new InvalidOperationException(
                            $"Structure node cap exceeded: more than {Inv(MaxNodes)} structure nodes.");
                    if (Res(nums[i]) is not PdfInteger key)
                    {
                        _treeProblems.Add("PARENTTREE BAD-KEY");
                        continue;
                    }

                    var k = (int)key.Value;
                    _tree.Add((k, nums[i + 1]));
                    if (!_treeByKey.TryAdd(k, nums[i + 1]))
                        _treeProblems.Add($"DUP-KEY {Inv(k)}");
                }
            }

            if (Res(node.Get(PdfName.Kids)) is PdfArray kids)
            {
                for (var i = 0; i < kids.Count; i++)
                {
                    if (Res(kids[i]) is PdfDictionary child)
                        WalkNumberTree(child, depth + 1, seen);
                    else
                        _treeProblems.Add("PARENTTREE UNRESOLVED");
                }
            }
        }

        private Elem? TreeElemFromArray(int key, int mcid)
        {
            if (!_treeByKey.TryGetValue(key, out var v) || Res(v) is not PdfArray arr || mcid < 0 || mcid >= arr.Count)
                return null;
            return Res(arr[mcid]) is PdfDictionary d && _elemByDict.TryGetValue(d, out var e) ? e : null;
        }

        private Elem? TreeElemSingle(int key)
        {
            if (!_treeByKey.TryGetValue(key, out var v))
                return null;
            return Res(v) is PdfDictionary d && _elemByDict.TryGetValue(d, out var e) ? e : null;
        }

        // ── Annotations ────────────────────────────────────────────────────────────────────────

        private List<string> DumpAnnotations(PdfReadPage page, int pageNo, List<(int Page, int Index, PdfDictionary Dict)> all)
        {
            var lines = new List<string>();
            if (Res(page.Dictionary.Get(PdfName.Annots)) is not PdfArray arr)
                return lines;

            for (var i = 0; i < arr.Count; i++)
            {
                if (Res(arr[i]) is not PdfDictionary a)
                {
                    lines.Add($"annot {Inv(i)}: UNRESOLVED");
                    continue;
                }

                all.Add((pageNo, i, a));
                var sub = Res(a.Get(PdfName.Subtype)) is PdfName sn ? sn.Value : "?";
                var sb = new StringBuilder($"annot {Inv(i)}: Subtype={sub}");
                if (Res(a.Get(new PdfName("StructParent"))) is PdfInteger sp)
                {
                    var e = TreeElemSingle((int)sp.Value);
                    var target = e is not null ? e.Path : _treeByKey.ContainsKey((int)sp.Value) ? "NOT-AN-ELEMENT" : "NO-ENTRY";
                    sb.Append($" StructParent={Inv(sp.Value)} -> {target}");
                }
                else
                {
                    sb.Append(" StructParent=none");
                }

                sb.Append(" Contents=").Append(a.Get(PdfName.Contents) is null ? "absent" : "present");
                sb.Append(" QuadPoints=").Append(a.Get(new PdfName("QuadPoints")) is null ? "absent" : "present");
                sb.Append(" dest=").Append(DestText(a));
                lines.Add(sb.ToString());
            }

            return lines;
        }

        private string DestText(PdfDictionary annot)
        {
            if (annot.Get(new PdfName("Dest")) is { } dest)
                return DestFrom(dest);

            if (Res(annot.Get(new PdfName("A"))) is PdfDictionary action)
            {
                var s = Res(action.Get(KeyS)) is PdfName sn ? sn.Value : "?";
                return s switch
                {
                    "GoTo" => action.Get(new PdfName("D")) is { } d ? DestFrom(d) : "GoTo none",
                    "URI" => "URI",
                    _ => "action " + s,
                };
            }

            return "none";
        }

        private string DestFrom(PdfObject dest)
        {
            var r = Res(dest);
            switch (r)
            {
                case PdfArray arr when arr.Count > 0:
                    var page = arr[0] is PdfIndirectReference pr && _pageByObj.TryGetValue(pr.ObjectNumber, out var n) ? "p" + Inv(n) : "p?";
                    var fit = arr.Count > 1 && Res(arr[1]) is PdfName fn ? fn.Value : "?";
                    return $"GoTo {page} {fit}";
                case PdfName name:
                    return $"named /{name.Value}";
                case PdfLiteralString or PdfHexString:
                    return "named " + Fmt(r, 0);
                case PdfDictionary dd when Res(dd.Get(new PdfName("D"))) is { } inner:
                    return DestFrom(inner);
                default:
                    return "unresolved";
            }
        }

        // ── Content ────────────────────────────────────────────────────────────────────────────

        private PageSection RunPage(PdfReadPage page, int pageNo)
        {
            var section = new PageSection();
            var pageKey = new StreamKey(pageNo, -1);
            var info = GetStream(pageKey, "p" + Inv(pageNo));
            var interpreter = new ContentInterpreter(_reader);
            var sink = _reader.CreateContentDiagnosticScope();
            var visitor = new ContentDumper(this, interpreter, section, pageNo, info);
            interpreter.Run(page, visitor, sink);
            foreach (var d in sink.Diagnostics)
                section.Lines.Add("DIAG " + d.Code.ToString());
            return section;
        }

        private StreamInfo GetStream(StreamKey key, string label)
        {
            if (_streamByKey.TryGetValue(key, out var s))
                return s;
            s = new StreamInfo { Key = key, Label = label };
            _streamByKey[key] = s;
            _streams.Add(s);
            return s;
        }

        private sealed class Frame
        {
            internal int Base;
            internal int Open;
            internal StreamInfo? Record;
        }

        private sealed class ContentDumper(
            Session s, ContentInterpreter interp, PageSection section, int pageNo, StreamInfo pageStream) : IContentVisitor
        {
            private const int MaxTextChars = 200;
            private readonly Stack<Frame> _frames = new([new Frame { Record = pageStream }]);
            private readonly Dictionary<PdfDictionary, FontDecoder> _fonts = new(ReferenceEqualityComparer.Instance);
            private int _depthTotal;

            private Frame Cur => _frames.Peek();

            private void Line(string text) => section.Lines.Add(new string(' ', (Cur.Base + Cur.Open) * 2) + text);

            private void Paint(string text) => Line(Cur.Open == 0 ? "UNMARKED " + text : text);

            public void OnOperator(string operatorName, IReadOnlyList<PdfObject> operands, int offset)
            {
                switch (operatorName)
                {
                    case "BMC":
                        Line("BMC " + (operands.Count > 0 ? s.Fmt(operands[0], 0) : "?"));
                        Push();
                        break;
                    case "BDC":
                        BeginDictionarySequence(operands);
                        break;
                    case "EMC":
                        if (Cur.Open == 0)
                        {
                            Line("UNBALANCED-EMC");
                        }
                        else
                        {
                            Cur.Open--;
                            _depthTotal--;
                        }

                        break;
                    case "Tj" when operands.Count >= 1:
                        Paint("show " + Show([operands[0]]));
                        break;
                    case "'" when operands.Count >= 1:
                        Paint("show " + Show([operands[0]]));
                        break;
                    case "\"" when operands.Count >= 3:
                        Paint("show " + Show([operands[2]]));
                        break;
                    case "TJ" when operands.Count >= 1 && operands[0] is PdfArray arr:
                        Paint("show " + Show(Enumerable.Range(0, arr.Count).Select(i => arr[i])));
                        break;
                    case "S" or "s":
                        Paint("path stroke");
                        break;
                    case "f" or "F" or "f*":
                        Paint("path fill");
                        break;
                    case "B" or "B*" or "b" or "b*":
                        Paint("path fill+stroke");
                        break;
                    case "sh":
                        Paint("path shade");
                        break;
                }
            }

            private void Push()
            {
                Cur.Open++;
                if (++_depthTotal > MaxMarkedContentDepth)
                    throw new InvalidOperationException(
                        $"Marked-content depth cap exceeded: nesting deeper than {Inv(MaxMarkedContentDepth)}.");
            }

            private void BeginDictionarySequence(IReadOnlyList<PdfObject> operands)
            {
                var tag = operands.Count > 0 ? s.Fmt(operands[0], 0) : "?";
                var props = operands.Count > 1 ? operands[1] : null;
                PdfDictionary? dict = props as PdfDictionary;
                if (props is PdfName pn)
                {
                    dict = interp.CurrentResources is { } res
                        && s.Res(res.Get(new PdfName("Properties"))) is PdfDictionary pd
                        && s.Res(pd.Get(pn)) is PdfDictionary found
                        ? found
                        : null;
                }

                var text = dict is not null ? s.Fmt(dict, 0) : props is PdfName n2 ? "unresolved-properties /" + n2.Value : "?";
                Line($"BDC {tag} {text}");
                if (dict is not null && s.Res(dict.Get(KeyMcid)) is PdfInteger mc)
                    Cur.Record?.Mcids.Add((int)mc.Value);
                Push();
            }

            public void OnInlineImage(PdfDictionary dictionary, ReadOnlyMemory<byte> data, int offset) =>
                Paint($"inline-image {Dim(dictionary)}");

            public void OnImageXObject(ParsedStream stream, int offset) =>
                Paint($"image {Dim(stream.Dictionary)}");

            private string Dim(PdfDictionary d)
            {
                var w = s.Res(d.Get(new PdfName("Width"))) is PdfInteger wi ? Inv(wi.Value) : "?";
                var h = s.Res(d.Get(new PdfName("Height"))) is PdfInteger hi ? Inv(hi.Value) : "?";
                return $"{w}x{h}";
            }

            public void OnFormBegin(PdfDictionary formDictionary, Matrix formMatrix, PdfRectangle? boundingBox, int objectNumber, int offset)
            {
                if (!section.Forms.TryGetValue(objectNumber, out var n))
                {
                    n = section.Forms.Count + 1;
                    section.Forms[objectNumber] = n;
                }

                var label = "form" + Inv(n);
                s._formLabels.TryAdd(objectNumber, "p" + Inv(pageNo) + " " + label);
                Paint(label);

                // Only the first invocation of a form, document-wide, records its MCIDs: drawing one
                // form twice repeats its MCIDs legitimately.
                var key = new StreamKey(-1, objectNumber);
                var first = !s._streamByKey.ContainsKey(key);
                var stream = s.GetStream(key, s._formLabels[objectNumber]);
                stream.FormDict ??= formDictionary;
                _frames.Push(new Frame { Base = Cur.Base + Cur.Open + 1, Record = first ? stream : null });
            }

            public void OnFormEnd(int objectNumber)
            {
                if (_frames.Count > 1)
                    _depthTotal -= _frames.Pop().Open;
            }

            // ── Text ──────────────────────────────────────────────────────────────────────────

            private string Show(IEnumerable<PdfObject> operands)
            {
                var bytes = new List<byte>();
                foreach (var o in operands)
                {
                    switch (o)
                    {
                        case PdfLiteralString ls:
                            bytes.AddRange(ls.Bytes.ToArray());
                            break;
                        case PdfHexString hs:
                            bytes.AddRange(hs.Bytes.ToArray());
                            break;
                    }
                }

                var decoder = FontFor(interp.GraphicsState.Font, interp.GraphicsState.FontResources);
                if (decoder?.Decode(bytes.ToArray()) is { } text)
                    return "\"" + Cap(Esc(text)) + "\"";

                var hex = Convert.ToHexString(bytes.ToArray());
                return "<" + (hex.Length > MaxTextChars ? hex[..MaxTextChars] + "…(+" + Inv(hex.Length - MaxTextChars) + ")" : hex) + ">";
            }

            private static string Cap(string escaped) =>
                escaped.Length > MaxTextChars
                    ? escaped[..MaxTextChars] + "…(+" + Inv(escaped.Length - MaxTextChars) + ")"
                    : escaped;

            private FontDecoder? FontFor(PdfObject? fontOperand, PdfDictionary? resources)
            {
                PdfDictionary? fontDict = null;
                if (fontOperand is PdfName name)
                {
                    if (resources is not null
                        && s.Res(resources.Get(PdfName.Font)) is PdfDictionary fonts
                        && s.Res(fonts.Get(name)) is PdfDictionary found)
                    {
                        fontDict = found;
                    }
                }
                else if (fontOperand is not null)
                {
                    fontDict = s.Res(fontOperand) as PdfDictionary;
                }

                if (fontDict is null)
                    return null;
                if (_fonts.TryGetValue(fontDict, out var cached))
                    return cached;

                var decoder = new FontDecoder();
                var subtype = s.Res(fontDict.Get(PdfName.Subtype)) is PdfName st ? st.Value : "";
                if (subtype == "Type0")
                {
                    decoder.Width = s.Res(fontDict.Get(PdfName.Encoding)) is PdfName enc && enc.Value is "Identity-H" or "Identity-V" ? 2 : 0;
                }
                else
                {
                    decoder.Width = 1;
                }

                if (fontDict.Get(new PdfName("ToUnicode")) is PdfIndirectReference tu
                    && s._reader.ResolveStream(tu) is { } stream
                    && s._reader.GetDecodedStreamData(stream) is { } data)
                {
                    decoder.Map = ToUnicodeMap.Parse(data);
                }

                _fonts[fontDict] = decoder;
                return decoder;
            }
        }

        private sealed class FontDecoder
        {
            internal ToUnicodeMap? Map;
            internal int Width;

            internal string? Decode(byte[] bytes)
            {
                if (Map is null || Width == 0)
                    return null;

                var sb = new StringBuilder();
                for (var i = 0; i < bytes.Length; i += Width)
                {
                    uint code = 0;
                    for (var j = i; j < Math.Min(i + Width, bytes.Length); j++)
                        code = (code << 8) | bytes[j];
                    sb.Append(Map.TryMap(code, out var text) ? text : "�");
                }

                return sb.ToString();
            }
        }

        // ── Checks ─────────────────────────────────────────────────────────────────────────────

        private void RunChecks(List<(int Page, int Index, PdfDictionary Dict)> annots)
        {
            var pages = _reader.Pages;

            // Both keys on one object (Table 326).
            for (var i = 0; i < pages.Count; i++)
            {
                var d = pages[i].Dictionary;
                if (d.Get(new PdfName("StructParent")) is not null && d.Get(new PdfName("StructParents")) is not null)
                    _problems.Add("BOTH-STRUCTPARENT(S) p" + Inv(i + 1));
            }

            foreach (var a in annots)
            {
                if (a.Dict.Get(new PdfName("StructParent")) is not null && a.Dict.Get(new PdfName("StructParents")) is not null)
                    _problems.Add($"BOTH-STRUCTPARENT(S) annot p{Inv(a.Page)} #{Inv(a.Index)}");
            }

            // Streams that only the structure side mentions still need a label.
            foreach (var (key, _, _) in _structMcids)
            {
                if (!_streamByKey.ContainsKey(key))
                    GetStream(key, key.Page > 0 ? "p" + Inv(key.Page) : key.FormObj >= 0 ? "form?" : "p?");
            }

            foreach (var st in _streams.ToList())
                CheckStream(st);

            CheckObjrs(annots);
        }

        private void CheckStream(StreamInfo st)
        {
            var structSide = _structMcids.Where(m => m.Key == st.Key).ToList();
            var dict = st.Key.Page > 0 && st.Key.Page <= _reader.Pages.Count
                ? _reader.Pages[st.Key.Page - 1].Dictionary
                : st.FormDict;

            if (dict is not null && dict.Get(new PdfName("StructParent")) is not null
                && dict.Get(new PdfName("StructParents")) is not null && st.Key.FormObj >= 0)
            {
                _problems.Add("BOTH-STRUCTPARENT(S) " + st.Label);
            }

            foreach (var g in st.Mcids.GroupBy(m => m).Where(g => g.Count() > 1).OrderBy(g => g.Key))
                _problems.Add($"DUP-MCID mcid {Inv(g.Key)} {st.Label}");

            int? sp = dict is not null && Res(dict.Get(new PdfName("StructParents"))) is PdfInteger spi ? (int)spi.Value : null;
            if (sp is null && st.Mcids.Count > 0)
                _problems.Add("NO-STRUCTPARENTS " + st.Label);

            var all = st.Mcids.Concat(structSide.Select(m => m.Mcid)).Distinct().OrderBy(m => m);
            foreach (var mcid in all)
            {
                var inContent = st.Mcids.Contains(mcid);
                var holders = structSide.Where(m => m.Mcid == mcid).Select(m => m.Owner).ToList();
                if (!inContent)
                {
                    _problems.Add($"MISSING mcid {Inv(mcid)} {st.Label} in {holders[0].Path}");
                    continue;
                }

                if (holders.Count == 0)
                {
                    _problems.Add($"ORPHAN mcid {Inv(mcid)} {st.Label}");
                    continue;
                }

                if (holders.Count > 1)
                    _problems.Add($"DUP-HOLDER mcid {Inv(mcid)} {st.Label}: " + string.Join(", ", holders.Select(h => h.Path)));

                if (sp is { } key)
                {
                    var pt = TreeElemFromArray(key, mcid);
                    if (!ReferenceEquals(pt, holders[0]))
                        _problems.Add(
                            $"PARENTTREE MISMATCH mcid {Inv(mcid)} {st.Label}: parent tree has {pt?.Path ?? "nothing"}, element holding it is {holders[0].Path}");
                }
            }
        }

        private void CheckObjrs(List<(int Page, int Index, PdfDictionary Dict)> annots)
        {
            var referenced = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var (owner, annot, _) in _objrs)
            {
                var at = annot is null ? -1 : annots.FindIndex(a => ReferenceEquals(a.Dict, annot));
                if (annot is null || at < 0)
                {
                    _problems.Add($"PARENTTREE MISMATCH OBJR in {owner.Path}: target is not an annotation on any page");
                    continue;
                }

                referenced.Add(annot);
                var label = $"annot p{Inv(annots[at].Page)} #{Inv(annots[at].Index)}";
                if (Res(annot.Get(new PdfName("StructParent"))) is not PdfInteger sp)
                {
                    _problems.Add($"PARENTTREE MISMATCH {label}: annotation has no /StructParent, element holding the OBJR is {owner.Path}");
                    continue;
                }

                var pt = TreeElemSingle((int)sp.Value);
                if (!ReferenceEquals(pt, owner))
                    _problems.Add($"PARENTTREE MISMATCH {label}: parent tree has {pt?.Path ?? "nothing"}, element holding the OBJR is {owner.Path}");
            }

            foreach (var a in annots)
            {
                if (a.Dict.Get(new PdfName("StructParent")) is not null && !referenced.Contains(a.Dict))
                    _problems.Add($"NO-OBJR annot p{Inv(a.Page)} #{Inv(a.Index)}");
            }
        }

        // ── Value formatting ───────────────────────────────────────────────────────────────────

        private const int MaxFormatDepth = 6;
        private const int MaxFormatItems = 32;

        private PdfObject? Res(PdfObject? o)
        {
            try
            {
                return o is null ? null : _reader.ResolveValue(o);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private static IEnumerable<KeyValuePair<PdfName, PdfObject>> Entries(PdfDictionary d) => d.Entries;

        internal string Fmt(PdfObject? o, int depth)
        {
            switch (o)
            {
                case null:
                case PdfNull:
                    return "null";
                case PdfName n:
                    return "/" + n.Value;
                case PdfInteger i:
                    return Inv(i.Value);
                case PdfReal r:
                    return Real(r.Value);
                case PdfBoolean b:
                    return b.Value ? "true" : "false";
                case PdfLiteralString ls:
                    return "\"" + Esc(TextString(ls.Bytes.Span)) + "\"";
                case PdfHexString hs:
                    return "\"" + Esc(TextString(hs.Bytes.Span)) + "\"";
                case PdfArray a:
                    if (depth >= MaxFormatDepth)
                        return "[…]";
                    var items = Enumerable.Range(0, Math.Min(a.Count, MaxFormatItems)).Select(i => Fmt(a[i], depth + 1));
                    var more = a.Count > MaxFormatItems ? " …(+" + Inv(a.Count - MaxFormatItems) + ")" : "";
                    return "[" + string.Join(' ', items) + more + "]";
                case PdfDictionary d:
                    if (depth >= MaxFormatDepth)
                        return "{…}";
                    var sorted = d.Entries.OrderBy(kv => kv.Key.Value, StringComparer.Ordinal).ToList();
                    var parts = sorted.Take(MaxFormatItems).Select(kv => kv.Key.Value + "=" + Fmt(kv.Value, depth + 1));
                    var moreD = sorted.Count > MaxFormatItems ? " …(+" + Inv(sorted.Count - MaxFormatItems) + ")" : "";
                    return "{" + string.Join(' ', parts) + moreD + "}";
                case PdfIndirectReference ir:
                    return RefText(ir);
                default:
                    return "<" + o.GetType().Name + ">";
            }
        }

        // An indirect reference prints as the index path when it names a structure element, and as
        // "<ref Type/Subtype>" otherwise; it is never followed any further and never shows a number.
        private string RefText(PdfIndirectReference r)
        {
            var target = Res(r);
            if (target is PdfDictionary d)
            {
                if (_elemByDict.TryGetValue(d, out var e))
                    return e.Path.Length > 0 ? e.Path : e.Type;
                var t = Res(d.Get(PdfName.Type)) is PdfName tn ? tn.Value : "-";
                var st = Res(d.Get(PdfName.Subtype)) is PdfName sn ? sn.Value : "-";
                return $"<ref {t}/{st}>";
            }

            return "<ref ?>";
        }

        // Table 323: /A is one attribute object or an array of attribute objects, each optionally
        // followed by a revision number; /C is the same for class names. A reference inside the list is
        // followed once so the attributes themselves show.
        private string FmtAttrList(PdfObject value)
        {
            var r = Res(value);
            if (r is PdfArray arr)
            {
                var sb = new StringBuilder("[");
                for (var i = 0; i < Math.Min(arr.Count, MaxFormatItems); i++)
                {
                    var item = Res(arr[i]);
                    if (i > 0 && !(item is PdfInteger))
                        sb.Append(' ');
                    sb.Append(item is PdfInteger rev ? "@" + Inv(rev.Value) : Fmt(item, 1));
                }

                if (arr.Count > MaxFormatItems)
                    sb.Append(" …(+" + Inv(arr.Count - MaxFormatItems) + ")");
                return sb.Append(']').ToString();
            }

            return Fmt(r ?? value, 0);
        }

        private static string TextString(ReadOnlySpan<byte> b)
        {
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(b[2..]);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return Encoding.UTF8.GetString(b[3..]);
            return Encoding.Latin1.GetString(b);
        }
    }

    private static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Real(double v)
    {
        if (double.IsNaN(v))
            return "NaN";
        if (double.IsInfinity(v))
            return v > 0 ? "Infinity" : "-Infinity";
        var s = v.ToString("0.##", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    // Anything a reader cannot see (controls, format characters such as the soft hyphen, line and
    // paragraph separators, non-ASCII spaces, lone surrogates, private use) prints as \u{XXXX}, so a
    // zero-width character in a golden is a visible diff.
    private static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (var rune in s.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(rune);
            if (rune.Value == 0xFFFD)
                sb.Append("\\u{FFFD}");
            else if (rune.Value == '\\')
                sb.Append("\\\\");
            else if (rune.Value == '"')
                sb.Append("\\\"");
            else if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned || (cat == UnicodeCategory.SpaceSeparator && rune.Value != ' '))
                sb.Append("\\u{").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture)).Append('}');
            else
                sb.Append(rune.ToString());
        }

        return sb.ToString();
    }
}
