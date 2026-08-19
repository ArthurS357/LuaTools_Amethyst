using System.Reflection;
using AwesomeAssertions;
using LuaToolsGui.Models;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins that the API DTOs stay write-once.
///
/// <para>
/// They are deserialization targets: System.Text.Json fills one from a response and the app reads it. A
/// settable property invites the failure this closes off — code that patches a field on the way through
/// ("the name came back empty, fill it in from Steam"), producing an object that no longer reports what
/// the server said while still being treated as if it did. Nothing was doing that; converting the whole
/// file to <c>init</c> changed no call site. This test is what keeps it that way, because re-opening one
/// property is a one-word edit that reads like a fix.
/// </para>
///
/// <para>
/// Reflection over the assembly rather than a list of type names, so a DTO added later is covered the day
/// it is added instead of the day someone remembers this file exists.
/// </para>
/// </summary>
public class ApiModelImmutabilityTests
{
    /// <summary>Every public class in the Models namespace that JSON is deserialized into.</summary>
    private static IEnumerable<Type> DtoTypes() =>
        typeof(GameDetails).Assembly
            .GetTypes()
            .Where(t => t.IsClass
                        && t.IsPublic
                        && t.Namespace == typeof(GameDetails).Namespace
                        && !t.IsAbstract);

    [Fact]
    public void No_api_dto_exposes_a_settable_property()
    {
        // An init-only property still has a setter in metadata; what distinguishes it is the
        // IsExternalInit modifier the compiler stamps on that setter's return type.
        var settable = DtoTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .Where(p => p.SetMethod is { IsPublic: true })
                              .Where(p => !p.SetMethod!.ReturnParameter
                                            .GetRequiredCustomModifiers()
                                            .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
                              .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        settable.Should().BeEmpty("a response the app received is a snapshot, not a working copy");
    }

    [Fact]
    public void The_sweep_actually_found_the_dtos()
    {
        // Without this, a namespace rename would turn the check above into a test that asserts nothing and
        // passes forever.
        DtoTypes().Should().Contain(typeof(HubcapManifestStatus))
                  .And.Contain(typeof(GameDetails))
                  .And.Contain(typeof(SupabaseSession));
    }

    [Fact]
    public void Init_still_lets_the_deserializer_fill_them()
    {
        // The whole point of init over a private setter: System.Text.Json can still populate the object.
        var status = System.Text.Json.JsonSerializer.Deserialize<HubcapManifestStatus>(
            """{"status":"available","manifest_file_exists":true,"needs_update":false}""")!;

        status.Status.Should().Be("available");
        status.ManifestFileExists.Should().BeTrue();
    }
}
