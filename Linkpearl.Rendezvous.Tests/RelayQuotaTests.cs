using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Un relais a un volume, une durée, et un plafond par adresse.</summary>
public sealed class RelayQuotaTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static byte[] Ticket(byte seed) => Enumerable.Repeat(seed, RendezvousTicket.SizeInBytes).ToArray();

    private static async Task<(TestClient A, TestClient B)> BridgeAsync(ServerHarness harness, byte seed)
    {
        var a = await harness.ConnectAsync();
        var b = await harness.ConnectAsync();
        await a.SendAsync(RendezvousWire.RelayOpen(Ticket(seed)));
        Assert.True(await a.IsSilentAsync(Short));
        await b.SendAsync(RendezvousWire.RelayOpen(Ticket(seed)));

        Assert.Equal(RendezvousKind.RelayReady, (await a.ReadFrameAsync())![0]);
        Assert.Equal(RendezvousKind.RelayReady, (await b.ReadFrameAsync())![0]);
        return (a, b);
    }

    [Fact]
    public async Task Un_relais_qui_depasse_son_volume_est_ferme()
    {
        await using var harness = await ServerHarness.StartAsync(RendezvousLimits.Default with { RelayByteQuota = 3_000 });

        var (a, b) = await BridgeAsync(harness, 0x11);
        var block = new byte[1_000];

        // Deux blocs dans un sens, un dans l'autre : le volume se compte sur
        // les deux ensemble, et le quatrième ne passe plus.
        await a.SendAsync(RendezvousWire.RelayData(block));
        Assert.NotNull(await b.ReadFrameAsync());
        await b.SendAsync(RendezvousWire.RelayData(block));
        Assert.NotNull(await a.ReadFrameAsync());

        await a.SendAsync(RendezvousWire.RelayData(block));
        await a.SendAsync(RendezvousWire.RelayData(block));

        // Le troisième bloc passe encore s'il tient, puis les deux côtés tombent.
        byte[]? frame;
        var received = 0;

        while ((frame = await b.ReadFrameAsync()) is not null)
            received += frame.Length;

        Assert.True(received < 2 * (block.Length + 1));
        Assert.True(await a.IsClosedAsync(TimeSpan.FromSeconds(5)));
        await ServerHarness.WaitUntilAsync(() => harness.Server.RelayQuotaCuts is 1);
    }

    [Fact]
    public async Task Un_relais_qui_depasse_sa_duree_est_ferme()
    {
        await using var harness = await ServerHarness.StartAsync(
            RendezvousLimits.Default with { RelayMaxDuration = TimeSpan.FromMilliseconds(500) });

        var (a, b) = await BridgeAsync(harness, 0x12);

        await a.SendAsync(RendezvousWire.RelayData([1]));
        Assert.Equal(new byte[] { 1 }, (await b.ReadFrameAsync())![1..]);

        Assert.True(await a.IsClosedAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await b.IsClosedAsync(TimeSpan.FromSeconds(5)));
        await ServerHarness.WaitUntilAsync(() => harness.Server.RelayQuotaCuts is 1);
        await ServerHarness.WaitUntilAsync(() => harness.Server.Snapshot().ActiveRelays is 0);
    }

    [Fact]
    public async Task Une_adresse_ne_tient_pas_plus_de_relais_que_le_plafond()
    {
        await using var harness = await ServerHarness.StartAsync(RendezvousLimits.Default with { MaxRelaysPerAddress = 2 });

        var first = await harness.ConnectAsync();
        await first.SendAsync(RendezvousWire.RelayOpen(Ticket(0x21)));
        var second = await harness.ConnectAsync();
        await second.SendAsync(RendezvousWire.RelayOpen(Ticket(0x22)));
        Assert.True(await second.IsSilentAsync(Short));

        var third = await harness.ConnectAsync();
        await third.SendAsync(RendezvousWire.RelayOpen(Ticket(0x23)));

        var refusal = await third.ReadFrameAsync();
        Assert.Equal(RendezvousKind.Error, refusal![0]);
        Assert.Contains("trop de relais", System.Text.Encoding.UTF8.GetString(refusal, 1, refusal.Length - 1));

        // Les places se reprennent quand les relais garés sont abandonnés.
        harness.Clock.Advance(RendezvousLimits.Default.RelayWaitTimeout + TimeSpan.FromSeconds(1));
        harness.Server.Sweep();
        await ServerHarness.WaitUntilAsync(() => harness.Server.Snapshot().Connections is 1);

        await third.SendAsync(RendezvousWire.RelayOpen(Ticket(0x23)));
        Assert.True(await third.IsSilentAsync(Short));
        Assert.Equal(1, harness.Server.Snapshot().RelayWaiting);
    }

    [Fact]
    public async Task Un_cote_qui_ne_prend_plus_rien_ferme_le_relais()
    {
        await using var harness = await ServerHarness.StartAsync(
            RendezvousLimits.Default with { RelayStallTimeout = TimeSpan.FromMilliseconds(300) });

        var a = await harness.ConnectAsync();
        var b = await harness.ConnectAsync(receiveBuffer: 4096);
        await a.SendAsync(RendezvousWire.RelayOpen(Ticket(0x31)));
        Assert.True(await a.IsSilentAsync(Short));
        await b.SendAsync(RendezvousWire.RelayOpen(Ticket(0x31)));
        Assert.Equal(RendezvousKind.RelayReady, (await a.ReadFrameAsync())![0]);

        // B ne lit plus rien : A pousse jusqu'à ce que le relais tombe.
        var block = RendezvousWire.RelayData(new byte[16 * 1024]);
        var started = DateTime.UtcNow;

        while (harness.Server.Snapshot().ActiveRelays is not 0)
        {
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "le relais n'a jamais été coupé");

            try
            {
                await a.SendAsync(block);
            }
            catch (IOException)
            {
                break;
            }
        }

        await ServerHarness.WaitUntilAsync(() => harness.Server.Snapshot().Connections is 0, TimeSpan.FromSeconds(10));
    }
}
