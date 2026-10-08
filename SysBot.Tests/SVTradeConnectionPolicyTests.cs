using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SysBot.Base;
using SysBot.Pokemon;
using Xunit;

namespace SysBot.Tests;

public class SVTradeConnectionPolicyTests
{
    [Theory]
    [InlineData(SwitchProtocol.WiFi)]
    [InlineData((SwitchProtocol)999)]
    public void LocalModeRejectsNonUsbProtocol(SwitchProtocol protocol)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new SVTradeConnectionPolicy(true, protocol));
        Assert.Contains("USB", exception.Message);
    }

    [Fact]
    public void LocalUsbModeDoesNotUseOnlineIdentity()
    {
        var policy = new SVTradeConnectionPolicy(true, SwitchProtocol.USB);
        Assert.True(policy.IsLocal);
        Assert.False(policy.UsesOnlineIdentity);
        Assert.True(policy.IsExpectedState(false));
        Assert.False(policy.IsExpectedState(true));
    }

    [Fact]
    public async Task LocalAlreadyOfflineDoesNotChangeConnection()
    {
        var connection = new ConnectionProbe(false);
        Assert.True(await Ensure(LocalPolicy(), connection));
        Assert.Equal(0, connection.ConnectCalls);
        Assert.Equal(0, connection.DisconnectCalls);
    }

    [Fact]
    public async Task LocalOnlineOnlyDisconnectsAndRecoveryUsesSamePolicy()
    {
        var policy = LocalPolicy();
        var connection = new ConnectionProbe(true);
        Assert.True(await Ensure(policy, connection));
        Assert.False(connection.Online);
        Assert.Equal(0, connection.ConnectCalls);
        Assert.Equal(1, connection.DisconnectCalls);

        connection.Online = true;
        Assert.True(await Ensure(policy, connection));
        Assert.False(connection.Online);
        Assert.Equal(0, connection.ConnectCalls);
        Assert.Equal(2, connection.DisconnectCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task LocalFailedOrUnverifiedDisconnectNeverConnects(bool reportedSuccess, bool changesState)
    {
        var connection = new ConnectionProbe(true)
        {
            TransitionResult = reportedSuccess,
            ChangesState = changesState,
        };
        Assert.False(await Ensure(LocalPolicy(), connection));
        Assert.True(connection.Online);
        Assert.Equal(0, connection.ConnectCalls);
        Assert.Equal(1, connection.DisconnectCalls);
    }

    [Fact]
    public async Task LocalDisconnectExceptionDoesNotFallBackToConnect()
    {
        var connection = new ConnectionProbe(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalPolicy().EnsureConnectionAsync(
            connection.IsOnline,
            connection.Connect,
            () => throw new InvalidOperationException("Disconnect failed")));
        Assert.Equal(0, connection.ConnectCalls);
    }

    [Theory]
    [InlineData(SwitchProtocol.WiFi)]
    [InlineData(SwitchProtocol.USB)]
    public async Task OnlineOfflineConnectsWithoutDisconnecting(SwitchProtocol protocol)
    {
        var policy = new SVTradeConnectionPolicy(false, protocol);
        var connection = new ConnectionProbe(false);
        Assert.False(policy.IsLocal);
        Assert.True(policy.UsesOnlineIdentity);
        Assert.True(await Ensure(policy, connection));
        Assert.True(connection.Online);
        Assert.Equal(1, connection.ConnectCalls);
        Assert.Equal(0, connection.DisconnectCalls);
    }

    [Fact]
    public async Task OnlineAlreadyConnectedDoesNotChangeConnection()
    {
        var connection = new ConnectionProbe(true);
        Assert.True(await Ensure(new SVTradeConnectionPolicy(false, SwitchProtocol.WiFi), connection));
        Assert.Equal(0, connection.ConnectCalls);
        Assert.Equal(0, connection.DisconnectCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlineFailedOrUnverifiedConnectReturnsFalse(bool reportedSuccess)
    {
        var connection = new ConnectionProbe(false) { TransitionResult = reportedSuccess, ChangesState = false };
        Assert.False(await Ensure(new SVTradeConnectionPolicy(false, SwitchProtocol.WiFi), connection));
        Assert.Equal(1, connection.ConnectCalls);
        Assert.Equal(0, connection.DisconnectCalls);
    }

    [Fact]
    public void OnlineOnlyGuardRejectsLocalMode()
    {
        Assert.Throws<InvalidOperationException>(() => LocalPolicy().RequireOnlineMode());
        new SVTradeConnectionPolicy(false, SwitchProtocol.WiFi).RequireOnlineMode();
    }

    [Theory]
    [InlineData(true, 0UL, true, true)]
    [InlineData(true, 123UL, true, true)]
    [InlineData(false, 123UL, true, false)]
    [InlineData(true, 123UL, false, false)]
    [InlineData(false, 123UL, false, false)]
    [InlineData(true, 0UL, false, false)]
    public void LocalPartnerRequiresBoxAndDistinctTrainer(bool inBox, ulong nid, bool distinctTrainer, bool expected)
    {
        Assert.Equal(expected, LocalPolicy().IsPartnerReady(inBox, nid, distinctTrainer));
    }

    [Theory]
    [InlineData(false, 123UL, false, true)]
    [InlineData(true, 123UL, true, true)]
    [InlineData(true, 0UL, true, false)]
    [InlineData(false, 0UL, false, false)]
    public void OnlinePartnerRetainsNidDetection(bool inBox, ulong nid, bool distinctTrainer, bool expected)
    {
        var policy = new SVTradeConnectionPolicy(false, SwitchProtocol.WiFi);
        Assert.Equal(expected, policy.IsPartnerReady(inBox, nid, distinctTrainer));
    }

    [Theory]
    [InlineData("", 123U, 456U, false)]
    [InlineData("   ", 123U, 456U, false)]
    [InlineData("Host", 123U, 456U, false)]
    [InlineData("Partner", 123U, 456U, true)]
    [InlineData("Host", 124U, 456U, true)]
    [InlineData("Host", 123U, 457U, true)]
    [InlineData("Partner", 0U, 0U, true)]
    public void DistinctTrainerRejectsBlankAndSelfButAllowsZeroIds(string name, uint tid, uint sid, bool expected)
    {
        var trainer = new TradeMyStatus();
        BinaryPrimitives.WriteUInt32LittleEndian(trainer.Data.AsSpan(0, 4), sid * 1_000_000 + tid);
        Encoding.Unicode.GetBytes(name).CopyTo(trainer.Data, 8);
        Assert.Equal(expected, SVTradeConnectionPolicy.IsDistinctTrainer(trainer, "Host", 123, 456));
    }

    [Fact]
    public void MissingJsonLocalSettingDefaultsToOnline()
    {
        Assert.False(new TradeSettings().PerformLocalTradeSV);
        var settings = JsonSerializer.Deserialize<TradeSettings>("{\"DisallowTradeEvolve\":false}");
        Assert.NotNull(settings);
        Assert.False(settings.PerformLocalTradeSV);
        Assert.False(settings.DisallowTradeEvolve);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void JsonRoundTripPreservesLocalModeAndEvolutionSetting(bool local, bool disallowEvolve)
    {
        var original = new TradeSettings { PerformLocalTradeSV = local, DisallowTradeEvolve = disallowEvolve };
        var restored = JsonSerializer.Deserialize<TradeSettings>(JsonSerializer.Serialize(original));
        Assert.NotNull(restored);
        Assert.Equal(local, restored.PerformLocalTradeSV);
        Assert.Equal(disallowEvolve, restored.DisallowTradeEvolve);
    }

    private static SVTradeConnectionPolicy LocalPolicy() => new(true, SwitchProtocol.USB);

    private static Task<bool> Ensure(SVTradeConnectionPolicy policy, ConnectionProbe connection) =>
        policy.EnsureConnectionAsync(connection.IsOnline, connection.Connect, connection.Disconnect);

    private sealed class ConnectionProbe(bool online)
    {
        public bool Online { get; set; } = online;
        public bool TransitionResult { get; init; } = true;
        public bool ChangesState { get; init; } = true;
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }

        public Task<bool> IsOnline() => Task.FromResult(Online);

        public Task<bool> Connect()
        {
            ConnectCalls++;
            if (ChangesState)
                Online = true;
            return Task.FromResult(TransitionResult);
        }

        public Task<bool> Disconnect()
        {
            DisconnectCalls++;
            if (ChangesState)
                Online = false;
            return Task.FromResult(TransitionResult);
        }
    }
}
