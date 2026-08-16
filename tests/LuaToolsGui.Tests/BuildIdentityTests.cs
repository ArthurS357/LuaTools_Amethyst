using System.Reflection;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Tests for <see cref="BuildIdentity"/>, the startup guard that warns when the running binary is not
/// LuaTools Amethyst.
///
/// <para>
/// The requirement it has to meet is asymmetric. Missing an official build costs a warning that never
/// appears; firing on the fork itself costs every user a false alarm on every launch, which is worse than
/// no check at all. So the important test here is the last one: the real, shipped assembly must classify
/// as Amethyst.
/// </para>
/// </summary>
public class BuildIdentityTests
{
    [Fact]
    public void ClassifiesTheAmethystMarker()
    {
        Assert.Equal(BuildKind.Amethyst, BuildIdentity.Classify("LuaTools Amethyst"));
    }

    [Theory]
    [InlineData("LuaTools")]          // the official product string
    [InlineData("Some Other App")]
    [InlineData("")]
    [InlineData(null)]
    public void ClassifiesAnythingElseAsUnrecognized(string? product)
    {
        Assert.Equal(BuildKind.Unrecognized, BuildIdentity.Classify(product));
    }

    [Fact]
    public void IsCaseInsensitiveAndToleratesSurroundingText()
    {
        // A packager appending a channel suffix ("LuaTools Amethyst (nightly)") must not trip the warning.
        Assert.Equal(BuildKind.Amethyst, BuildIdentity.Classify("luatools amethyst"));
        Assert.Equal(BuildKind.Amethyst, BuildIdentity.Classify("LuaTools Amethyst (nightly)"));
    }

    [Fact]
    public void TheShippedAssemblyIdentifiesAsAmethyst()
    {
        // The false-positive guard, and a live check on the csproj: if <Product> is ever changed or
        // dropped, every user gets a spurious "this may not be Amethyst" toast on launch. Reads the app
        // assembly (via a type from it), not the test assembly.
        string? product = typeof(BuildIdentity).Assembly
            .GetCustomAttribute<AssemblyProductAttribute>()?.Product;

        Assert.Equal(BuildIdentity.AmethystProductMarker, product);
        Assert.Equal(BuildKind.Amethyst, BuildIdentity.Classify(product));
    }
}
