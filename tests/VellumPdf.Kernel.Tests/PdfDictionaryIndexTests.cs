// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #208: <see cref="PdfDictionary"/> switches from a linear scan to an index once it grows past a
/// threshold, so building or looking up an n-entry dictionary is no longer quadratic in n. That
/// matters specifically for <c>/Encrypt</c>, where everything read out of it runs before a password
/// is checked, on a file anyone can send, but the fix lives entirely in <see cref="PdfDictionary"/>,
/// so it is tested here rather than only through the reader.
///
/// Before this fix, building a 140,000-key dictionary directly took two to three times this test's
/// 10s budget, measured on the development machine (Release build);
/// <see cref="VellumPdf.Reader.Tests.EncryptDictionaryDenialOfServiceTests"/>
/// pins the same defect reached the way #208 actually cares about — through a hostile
/// <c>/Encrypt</c> dictionary, before <c>PdfReader.Open</c> checks any password.
/// </summary>
public sealed class PdfDictionaryIndexTests
{
    private static string Serialize(PdfObject obj)
    {
        var ms = new MemoryStream();
        obj.WriteTo(new PdfWriter(ms));
        return System.Text.Encoding.ASCII.GetString(ms.ToArray());
    }

    /// <summary>
    /// 140,000 keys, well past the point where a linear scan would still finish in time: two to three
    /// times this test's 10s budget before this fix, measured on the development machine. Every key
    /// is read back afterwards to confirm the index (not just the build) is correct, not merely fast.
    /// </summary>
    // xUnit1069 wants TestContext.Current.CancellationToken threaded through so the Timeout can end
    // the test promptly; PdfDictionary.Set/TryGet take no CancellationToken, and there is nothing to
    // thread it into. The Timeout itself is the #208 regression pin — see the class doc — so it
    // stays rather than being dropped.
#pragma warning disable xUnit1069
    [Fact(Timeout = 10_000)]
    public void LargeDictionary_buildsAndLooksUp_underTimeout()
    {
        const int count = 140_000;
        var dict = new PdfDictionary();
        for (var i = 0; i < count; i++)
            dict.Set(new PdfName("K" + i), new PdfInteger(i));

        for (var i = 0; i < count; i += 997) // a sparse, non-sequential sample across the whole range
        {
            Assert.True(dict.TryGet(new PdfName("K" + i), out var value));
            var integer = Assert.IsType<PdfInteger>(value);
            Assert.Equal(i, integer.Value);
        }

        Assert.False(dict.TryGet(new PdfName("NotPresent"), out _));
    }
#pragma warning restore xUnit1069

    /// <summary>
    /// Insertion order survives crossing the index threshold, both in <c>Entries</c> and in the bytes
    /// <see cref="PdfDictionary.WriteTo"/> emits — golden output depends on the latter, so a fix that
    /// sped up lookup by silently reordering entries (e.g. building the index from a
    /// <see cref="Dictionary{TKey,TValue}"/> enumeration instead of the backing list) would still fail
    /// this even though every individual lookup answered correctly.
    /// </summary>
    [Fact]
    public void InsertionOrder_survivesCrossingTheIndexThreshold()
    {
        const int count = 24; // past PdfDictionary's 16-entry threshold
        var dict = new PdfDictionary();
        for (var i = 0; i < count; i++)
            dict.Set(new PdfName("K" + i), new PdfInteger(i));

        var entries = dict.Entries;
        Assert.Equal(count, entries.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal("K" + i, entries[i].Key.Value);
            Assert.Equal(i, Assert.IsType<PdfInteger>(entries[i].Value).Value);
        }

        var expected = "<<" + string.Concat(Enumerable.Range(0, count).Select(i => $"\n/K{i} {i}")) + "\n>>";
        Assert.Equal(expected, Serialize(dict));
    }

    /// <summary>
    /// Replacing an existing key once the index is active must change the value in place, not move it
    /// — <c>Set</c>'s own contract ("replacing any existing entry") predates the index and does not
    /// change because of it.
    /// </summary>
    [Fact]
    public void Set_replacingAnExistingKey_afterIndexBuilt_changesValueNotPosition()
    {
        const int count = 20; // past the 16-entry threshold, so _index is built
        var dict = new PdfDictionary();
        for (var i = 0; i < count; i++)
            dict.Set(new PdfName("K" + i), new PdfInteger(i));

        dict.Set(new PdfName("K5"), new PdfInteger(999));

        Assert.True(dict.TryGet(new PdfName("K5"), out var value));
        Assert.Equal(999, Assert.IsType<PdfInteger>(value).Value);

        var entries = dict.Entries;
        Assert.Equal(count, entries.Count); // no entry was added or removed
        Assert.Equal("K5", entries[5].Key.Value); // same position as before the replace
        Assert.Equal(999, Assert.IsType<PdfInteger>(entries[5].Value).Value);

        // every other key is untouched
        for (var i = 0; i < count; i++)
        {
            if (i == 5)
                continue;
            Assert.Equal(i, Assert.IsType<PdfInteger>(entries[i].Value).Value);
        }
    }

    /// <summary>
    /// A copy taken past the threshold is independent of its source: mutating the copy must not
    /// affect the source's lookups. <see cref="ShallowCopy_pastTheThreshold_carriesTheIndex"/> is the
    /// sibling that pins the index itself carrying across, which this test does not — every
    /// assertion here still passes with that line deleted, since a copy that fell back to a linear
    /// scan would still answer independently, just slower.
    /// </summary>
    [Fact]
    public void ShallowCopy_pastTheThreshold_isIndependentlyCorrect()
    {
        const int count = 20;
        var source = new PdfDictionary();
        for (var i = 0; i < count; i++)
            source.Set(new PdfName("K" + i), new PdfInteger(i));

        var copy = source.ShallowCopy();

        copy.Set(new PdfName("K5"), new PdfInteger(12345));
        copy.Set(new PdfName("NewKey"), new PdfInteger(-1));

        Assert.True(source.TryGet(new PdfName("K5"), out var sourceValue));
        Assert.Equal(5, Assert.IsType<PdfInteger>(sourceValue).Value);
        Assert.False(source.TryGet(new PdfName("NewKey"), out _));

        Assert.True(copy.TryGet(new PdfName("K5"), out var copyValue));
        Assert.Equal(12345, Assert.IsType<PdfInteger>(copyValue).Value);
        Assert.True(copy.TryGet(new PdfName("NewKey"), out var newValue));
        Assert.Equal(-1, Assert.IsType<PdfInteger>(newValue).Value);
    }

    /// <summary>
    /// <see cref="PdfDictionary.ShallowCopy"/> must carry the index across, or the copy silently
    /// falls back to a linear scan on every lookup. A sparse sample cannot show that: a stride wide
    /// enough to keep even a linear scan fast would not separate "index carried over" from "index
    /// dropped" within the time budget either way, so this reads back all 140,000 keys. Without the
    /// carry-over that is on the order of 140,000² comparisons — tens of seconds; with it, well under
    /// a second.
    /// </summary>
    // See LargeDictionary_buildsAndLooksUp_underTimeout above: same #208 pin, same reason the
    // CancellationToken xUnit1069 wants has nowhere to go.
#pragma warning disable xUnit1069
    [Fact(Timeout = 10_000)]
    public void ShallowCopy_pastTheThreshold_carriesTheIndex()
    {
        const int count = 140_000;
        var source = new PdfDictionary();
        for (var i = 0; i < count; i++)
            source.Set(new PdfName("K" + i), new PdfInteger(i));

        var copy = source.ShallowCopy();

        for (var i = 0; i < count; i++)
        {
            Assert.True(copy.TryGet(new PdfName("K" + i), out var value));
            Assert.Equal(i, Assert.IsType<PdfInteger>(value).Value);
        }
    }
#pragma warning restore xUnit1069

    /// <summary>
    /// A dictionary holding exactly <c>IndexThreshold</c> entries still answers correctly. At this
    /// count <c>BuildIndex</c> has not run — it fires only once a <c>Set</c> pushes past the
    /// threshold — so the linear scan is what adds the 16th entry and looks it back up.
    ///
    /// This pins behaviour at the boundary, not the fencepost itself: moving the check from
    /// <c>&gt;</c> to <c>&gt;=</c> would build the index one entry sooner and every assertion here
    /// would still pass, because both paths return the same answers. That is the point — the
    /// threshold is a tuning choice, and no observable behaviour may depend on which side of it a
    /// dictionary sits.
    /// </summary>
    [Fact]
    public void DictionaryAtExactlyTheThreshold_isStillCorrect()
    {
        const int count = 16; // PdfDictionary.IndexThreshold itself
        var dict = new PdfDictionary();
        for (var i = 0; i < count; i++)
            dict.Set(new PdfName("K" + i), new PdfInteger(i));

        for (var i = 0; i < count; i++)
        {
            Assert.True(dict.TryGet(new PdfName("K" + i), out var value));
            Assert.Equal(i, Assert.IsType<PdfInteger>(value).Value);
        }
        Assert.False(dict.TryGet(new PdfName("NotPresent"), out _));

        dict.Set(new PdfName("K5"), new PdfInteger(999));
        Assert.True(dict.TryGet(new PdfName("K5"), out var replaced));
        Assert.Equal(999, Assert.IsType<PdfInteger>(replaced).Value);
        Assert.Equal(count, dict.Entries.Count); // replace, not append
    }

    /// <summary>
    /// A dictionary copied while still below the threshold, then grown past it in the copy alone,
    /// must build its own index correctly and leave the source's (still below-threshold, still
    /// linear-scanning) state untouched — the transition has to work whether the index already
    /// existed at copy time (covered above) or is built later from a copy that started without one.
    /// </summary>
    [Fact]
    public void ShallowCopy_belowThreshold_thenGrownPastItInTheCopy_isCorrect()
    {
        const int initialCount = 10; // below the 16-entry threshold
        var source = new PdfDictionary();
        for (var i = 0; i < initialCount; i++)
            source.Set(new PdfName("K" + i), new PdfInteger(i));

        var copy = source.ShallowCopy();
        for (var i = initialCount; i < 30; i++) // grows the copy past the threshold
            copy.Set(new PdfName("K" + i), new PdfInteger(i));

        for (var i = 0; i < 30; i++)
        {
            Assert.True(copy.TryGet(new PdfName("K" + i), out var value));
            Assert.Equal(i, Assert.IsType<PdfInteger>(value).Value);
        }

        Assert.Equal(initialCount, source.Entries.Count);
        for (var i = 0; i < initialCount; i++)
            Assert.True(source.TryGet(new PdfName("K" + i), out _));
        Assert.False(source.TryGet(new PdfName("K" + initialCount), out _));
    }

    /// <summary>
    /// <see cref="ShallowCopy_pastTheThreshold_isIndependentlyCorrect"/> mutates the copy and re-reads
    /// the source; this checks the other direction, mutating the source after the copy is taken and
    /// re-reading the copy, so a shared <c>_entries</c> list or a shared <c>_index</c> instance would
    /// be caught either way the aliasing could happen.
    /// </summary>
    [Fact]
    public void ShallowCopy_pastTheThreshold_sourceMutationDoesNotReachTheCopy()
    {
        const int count = 20;
        var source = new PdfDictionary();
        for (var i = 0; i < count; i++)
            source.Set(new PdfName("K" + i), new PdfInteger(i));

        var copy = source.ShallowCopy();

        source.Set(new PdfName("K5"), new PdfInteger(54321));
        source.Set(new PdfName("NewKey"), new PdfInteger(-1));

        Assert.True(source.TryGet(new PdfName("K5"), out var sourceValue));
        Assert.Equal(54321, Assert.IsType<PdfInteger>(sourceValue).Value);

        Assert.True(copy.TryGet(new PdfName("K5"), out var copyValue));
        Assert.Equal(5, Assert.IsType<PdfInteger>(copyValue).Value);
        Assert.False(copy.TryGet(new PdfName("NewKey"), out _));
    }

    /// <summary>
    /// <c>Set</c>, <c>TryGet</c> and <c>Get</c> reject a <see langword="null"/> key rather than
    /// answering differently depending on which side of the index threshold the dictionary sits — see
    /// the comment on <c>Set</c> for why. Checked both below and above the threshold, since the guard
    /// existing at all is what makes the two sides agree; a regression that moved it into only one of
    /// the two branches would still pass a test that checked a single dictionary size.
    /// </summary>
    [Theory]
    [InlineData(4)] // below IndexThreshold: the linear-scan path
    [InlineData(20)] // above IndexThreshold: the _index path
    public void NullKey_throwsArgumentNullException_fromSetTryGetAndGet(int entryCount)
    {
        var dict = new PdfDictionary();
        for (var i = 0; i < entryCount; i++)
            dict.Set(new PdfName("K" + i), new PdfInteger(i));

        var setEx = Assert.Throws<ArgumentNullException>(() => dict.Set(null!, new PdfInteger(0)));
        Assert.Equal("key", setEx.ParamName);

        var tryGetEx = Assert.Throws<ArgumentNullException>(() => dict.TryGet(null!, out _));
        Assert.Equal("key", tryGetEx.ParamName);

        var getEx = Assert.Throws<ArgumentNullException>(() => dict.Get(null!));
        Assert.Equal("key", getEx.ParamName);
    }
}
