// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using VellumPdf.Testing;

namespace VellumPdf.Barcodes.Tests;

public sealed class StrongNamingTests
{
    [Fact]
    public void InternalsVisibleTo_publicKey_matchesActualSigningKey()
    {
        StrongNamePublicKeyAssertion.AssertGrantedByAll(
            Assembly.GetExecutingAssembly(),
            "VellumPdf.Barcodes");
    }
}
