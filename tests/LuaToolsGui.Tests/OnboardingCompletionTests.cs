using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// When onboarding counts as done. "Use manual code" closes the overlay without marking it (the choices were
/// never applied), so what keeps the welcome screen from coming back for good is a successful sign-in, by
/// either route. Giving up midway leaves it armed.
/// </summary>
public sealed class OnboardingCompletionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-onboard-" + Guid.NewGuid().ToString("N"));

    public OnboardingCompletionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // The constructor only stores its services; the paths under test touch the auth and cache ones.
    private (OnboardingViewModel Vm, AuthService Auth, CacheService Cache) Build()
    {
        var auth = new AuthService(new HttpClientHandler(), Path.Combine(_dir, "auth.dat"));
        var cache = new CacheService(Path.Combine(_dir, "cache"));
        var vm = new OnboardingViewModel(auth, cache, null!, null!, null!, null!, null!);
        return (vm, auth, cache);
    }

    [Fact]
    public async Task A_successful_sign_in_marks_onboarding_complete()
    {
        var (_, auth, cache) = Build();
        File.WriteAllBytes(Path.Combine(_dir, "auth.dat"), ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(new StoredAuth
            {
                RefreshToken = "r", AccessToken = "a", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            }), null, DataProtectionScope.CurrentUser));
        cache.OnboardingComplete.Should().BeFalse();

        (await auth.InitializeAsync()).Should().BeTrue();

        cache.OnboardingComplete.Should().BeTrue();
    }

    [Fact]
    public void Closing_through_manual_code_alone_keeps_onboarding_armed()
    {
        var (vm, _, cache) = Build();
        vm.IsOpen = true;

        vm.UseManualCodeCommand.Execute(null);

        vm.IsOpen.Should().BeFalse();
        cache.OnboardingComplete.Should().BeFalse("the user has not signed in or finished the setup");
    }
}
