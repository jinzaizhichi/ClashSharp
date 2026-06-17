/*
 * App Settings Service Tests
 * Verifies default user-facing settings
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/AppSettingsServiceTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests defaults exposed by application settings.</summary>
public sealed class AppSettingsServiceTests
{
    /// <summary>Verifies the default mixed proxy port avoids common proxy/VPN defaults.</summary>
    [Fact]
    public void MixedPort_DefaultsTo10000()
    {
        Assert.Equal(10000, AppSettingsService.Instance.MixedPort);
    }

    /// <summary>Verifies the default connection test URL matches the configured Clash# probe endpoint.</summary>
    [Fact]
    public void ConnectionTestUrl_DefaultsToGooleCom()
    {
        Assert.Equal("https://goole.com", AppSettingsService.Instance.ConnectionTestUrl);
    }

    /// <summary>Verifies mainland China URL blocking is controlled independently from display mode.</summary>
    [Fact]
    public void MainlandChinaUrlBlockingEnabled_DefaultsToFalse()
    {
        Assert.False(AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled);
    }

    /// <summary>Verifies reset clears persisted overrides back to their default values.</summary>
    [Fact]
    public void ResetAllSettings_RestoresDefaults()
    {
        AppSettingsService.Instance.MixedPort = 12000;
        AppSettingsService.Instance.ConnectionTestUrl = "https://example.com/generate_204";
        AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = true;

        AppSettingsService.Instance.ResetAllSettings();

        Assert.Equal(10000, AppSettingsService.Instance.MixedPort);
        Assert.Equal("https://goole.com", AppSettingsService.Instance.ConnectionTestUrl);
        Assert.False(AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled);
    }
}
