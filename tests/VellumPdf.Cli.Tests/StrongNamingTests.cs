// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using VellumPdf.Testing;

namespace VellumPdf.Cli.Tests;

public sealed class StrongNamingTests
{
    [Fact]
    public void InternalsVisibleTo_publicKey_matchesActualSigningKey()
    {
        // VellumPdf.Cli overrides <AssemblyName> to "vellum-preflight" (the dotnet-tool
        // command name); that's its real assembly identity for reflection purposes.
        StrongNamePublicKeyAssertion.AssertGrantedByAll(
            Assembly.GetExecutingAssembly(),
            "vellum-preflight");
    }
}
