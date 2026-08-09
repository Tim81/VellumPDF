// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;

namespace VellumPdf.Testing;

/// <summary>
/// Regression guard for #53: fails with a clear message if a declaring
/// assembly's InternalsVisibleTo public key drifts from the friend
/// assembly's actual signing key (e.g. a partial key rotation), rather than
/// relying solely on the compiler's CS0281 across every affected project.
/// </summary>
internal static class StrongNamePublicKeyAssertion
{
    public static void AssertGrantedByAll(Assembly ownAssembly, params string[] declaringAssemblySimpleNames)
    {
        var ownName = ownAssembly.GetName().Name!;
        var ownKeyHex = Convert.ToHexString(ownAssembly.GetName().GetPublicKey()!).ToLowerInvariant();
        Assert.NotEmpty(ownKeyHex);

        foreach (var declaringName in declaringAssemblySimpleNames)
        {
            var declaringAssembly = Assembly.Load(declaringName);
            var attribute = declaringAssembly
                .GetCustomAttributes<InternalsVisibleToAttribute>()
                .SingleOrDefault(a => a.AssemblyName.StartsWith(ownName + ",", StringComparison.Ordinal));

            Assert.True(attribute is not null, $"{declaringName} does not grant InternalsVisibleTo to {ownName}.");

            var keyMarker = "PublicKey=";
            var markerIndex = attribute!.AssemblyName.IndexOf(keyMarker, StringComparison.Ordinal);
            Assert.True(markerIndex >= 0, $"{declaringName}'s InternalsVisibleTo to {ownName} has no PublicKey.");

            var declaredKeyHex = attribute.AssemblyName[(markerIndex + keyMarker.Length)..].Trim().ToLowerInvariant();
            Assert.Equal(ownKeyHex, declaredKeyHex);
        }
    }
}
