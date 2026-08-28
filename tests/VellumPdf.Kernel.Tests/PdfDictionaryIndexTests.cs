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
/// Before this fix, building a 140,000-key dictionary directly took ~40.6s on the development
/// machine (Release build); <see cref="VellumPdf.Reader.Tests.EncryptDictionaryDenialOfServiceTests"/>
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
    /// 140,000 keys, well past the point where a linear scan would still finish in time: measured at
    /// ~40.6s before this fix, against this test's 10s budget. Every key is read back afterwards to
    /// confirm the index (not just the build) is correct, not merely fast.
    /// </summary>
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
    /// <see cref="PdfDictionary.ShallowCopy"/> must carry the index across, or the copy silently falls
    /// back to a linear scan — a performance regression, not a correctness one, but still worth
    /// pinning since ShallowCopy runs on every stream write. Checked by confirming the copy is
    /// independent: mutating the copy after the copy must not affect the source's lookups.
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
}
