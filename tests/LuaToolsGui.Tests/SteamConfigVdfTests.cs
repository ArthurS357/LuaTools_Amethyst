using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins <see cref="SteamConfigVdf"/>, the parser half of what upstream keeps inside its key-DONATION
/// service. The upload half is deliberately absent from this fork, so these tests also stand as the record
/// that what was ported reads config.vdf and nothing else.
/// </summary>
public class SteamConfigVdfTests
{
    private const string ValidKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherKey = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    /// <summary>The shape Steam actually writes: keys nest several levels down under depots/&lt;id&gt;.</summary>
    private static string Vdf(string depots) => $$"""
        "InstallConfigStore"
        {
            "Software"
            {
                "Valve"
                {
                    "Steam"
                    {
                        "depots"
                        {
        {{depots}}
                        }
                    }
                }
            }
        }
        """;

    // ── Extraction ────────────────────────────────────────────────────────────

    [Fact]
    public void Finds_a_key_nested_deep_under_depots()
    {
        var keys = SteamConfigVdf.ExtractKeys(Vdf($$"""
            "228990"
            {
                "DecryptionKey"     "{{ValidKey}}"
            }
            """));

        keys.Should().ContainSingle()
            .Which.Should().Be(new DepotKey(228990, ValidKey));
    }

    [Fact]
    public void Finds_every_key_in_the_document()
    {
        var keys = SteamConfigVdf.ExtractKeys(Vdf($$"""
            "1001"
            {
                "DecryptionKey"     "{{ValidKey}}"
            }
            "1002"
            {
                "DecryptionKey"     "{{OtherKey}}"
            }
            """));

        keys.Should().BeEquivalentTo(new[] { new DepotKey(1001, ValidKey), new DepotKey(1002, OtherKey) });
    }

    [Fact]
    public void Ignores_a_depot_section_that_carries_no_key()
    {
        var keys = SteamConfigVdf.ExtractKeys(Vdf($$"""
            "1001"
            {
                "manifests"
                {
                    "public"    "12345"
                }
            }
            "1002"
            {
                "DecryptionKey"     "{{ValidKey}}"
            }
            """));

        keys.Should().ContainSingle().Which.DepotId.Should().Be(1002);
    }

    [Fact]
    public void Skips_line_comments()
    {
        var keys = SteamConfigVdf.ExtractKeys(Vdf($$"""
            // "9999" { "DecryptionKey" "{{OtherKey}}" }
            "1001"
            {
                "DecryptionKey"     "{{ValidKey}}"     // trailing note
            }
            """));

        keys.Should().ContainSingle().Which.DepotId.Should().Be(1001);
    }

    // ── Validation: a pair that cannot be used must not be reported ────────────

    [Theory]
    [InlineData("0123456789abcdef")]                                                    // too short
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefAA")]  // too long
    [InlineData("")]
    public void Rejects_a_key_that_is_not_64_characters(string key)
    {
        SteamConfigVdf.ExtractKeys(Vdf($$"""
            "1001"
            {
                "DecryptionKey"     "{{key}}"
            }
            """)).Should().BeEmpty();
    }

    [Fact]
    public void Rejects_a_64_character_key_that_is_not_hex()
    {
        // 64 chars, alphanumeric, but not hex. Upstream's ^[a-zA-Z0-9]{64}$ accepts this; the value then
        // throws FormatException in Convert.FromHexString the moment a download tries to use it.
        const string notHex = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
        notHex.Length.Should().Be(64);

        SteamConfigVdf.ExtractKeys(Vdf($$"""
            "1001"
            {
                "DecryptionKey"     "{{notHex}}"
            }
            """)).Should().BeEmpty();
    }

    [Fact]
    public void Every_extracted_key_survives_hex_decoding()
    {
        // The contract the validation exists for: anything ExtractKeys returns is usable as AES-256 bytes.
        var keys = SteamConfigVdf.ExtractKeys(Vdf($$"""
            "1001"
            {
                "DecryptionKey"     "{{ValidKey}}"
            }
            """));

        foreach (var k in keys)
            Convert.FromHexString(k.Key).Should().HaveCount(32);
    }

    [Theory]
    [InlineData("notanumber")]
    [InlineData("12345678901")] // 11 digits — past what an appid/depot id can be
    public void Rejects_a_section_name_that_is_not_a_depot_id(string name)
    {
        SteamConfigVdf.ExtractKeys(Vdf($$"""
            "{{name}}"
            {
                "DecryptionKey"     "{{ValidKey}}"
            }
            """)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("1001", ValidKey, true)]
    [InlineData("1001", "short", false)]
    [InlineData("abc", ValidKey, false)]
    [InlineData(null, ValidKey, false)]
    [InlineData("1001", null, false)]
    public void IsValidPair_gates_on_both_halves(string? depotId, string? key, bool expected) =>
        SteamConfigVdf.IsValidPair(depotId, key).Should().Be(expected);

    // ── Escaped quotes: the upstream parser loses everything after one ─────────

    [Fact]
    public void An_escaped_quote_inside_a_value_does_not_desynchronise_the_parser()
    {
        // Upstream scans for the next raw '"', so the token ends early and every following key/value pair
        // is read one position out of step — silently dropping the depot key below it.
        var keys = SteamConfigVdf.ExtractKeys(Vdf($$"""
            "0"
            {
                "note"      "a \"quoted\" word"
            }
            "1001"
            {
                "DecryptionKey"     "{{ValidKey}}"
            }
            """));

        keys.Should().ContainSingle().Which.DepotId.Should().Be(1001);
    }

    [Fact]
    public void An_escaped_backslash_is_unescaped_not_treated_as_an_escape()
    {
        var root = SteamConfigVdf.ParseVdf("""
            "root"
            {
                "path"      "C:\\Program Files\\Steam"
            }
            """);

        var section = root.Children["root"].Should().BeOfType<VdfNode.Section>().Subject;
        section.Children["path"].Should().BeOfType<VdfNode.Leaf>()
            .Which.Value.Should().Be(@"C:\Program Files\Steam");
    }

    // ── Malformed input must degrade, never throw ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a vdf at all")]
    [InlineData("\"unterminated")]
    [InlineData("\"a\" { \"b\" { \"c\"")]  // truncated mid-nesting
    [InlineData("}}}}")]                   // more closes than opens
    public void Malformed_input_yields_no_keys_and_does_not_throw(string? content)
    {
        var act = () => SteamConfigVdf.ExtractKeys(content);

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Fact]
    public void A_missing_config_vdf_is_the_callers_empty_string_and_yields_nothing()
    {
        // ResolveKeys guards File.Exists and never reads a missing path, so the contract this parser has to
        // honour is the degenerate one: no content, no keys, no exception.
        SteamConfigVdf.ExtractKeys(null).Should().BeEmpty();
        SteamConfigVdf.ExtractKeys(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Pathological_nesting_does_not_overflow_the_stack()
    {
        // A hand-edited or corrupt file must not be able to turn a key lookup into a process kill.
        string deep = string.Concat(Enumerable.Repeat("\"n\"\n{\n", 5000))
                      + string.Concat(Enumerable.Repeat("}\n", 5000));

        var act = () => SteamConfigVdf.ExtractKeys(deep);

        act.Should().NotThrow();
    }
}
