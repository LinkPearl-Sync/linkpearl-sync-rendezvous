using System.Net;
using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Les places de connexion, et qui peut les occuper toutes.</summary>
public sealed class ConnectionSlotsTests
{
    private static (string Bucket, string? Wide) Of(string address)
    {
        var ip = IPAddress.Parse(address);
        return (AddressBucket.Of(ip), AddressBucket.Wide(ip));
    }

    [Fact]
    public void Un_48_ne_prend_pas_plus_que_son_plafond_meme_reparti_sur_des_64()
    {
        // Un hébergeur donne un /48 : 65 536 /64, chacun sous le plafond par
        // adresse, occupaient toutes les places du service.
        var slots = new ConnectionSlots();
        var limits = new RendezvousLimits { MaxConnectionsPerAddress = 2, MaxConnectionsPerPrefix = 5 };
        var taken = 0;

        for (var subnet = 0; subnet < 10; subnet++)
        {
            var (bucket, wide) = Of($"2001:db8:7:{subnet:x}::1");

            for (var i = 0; i < 2; i++)
                taken += slots.TryReserve(bucket, wide, limits) ? 1 : 0;
        }

        Assert.Equal(5, taken);
        Assert.Equal(5, slots.Total);

        // Un autre /48 n'en pâtit pas.
        var (other, otherWide) = Of("2001:db8:8::1");
        Assert.True(slots.TryReserve(other, otherWide, limits));
    }

    [Fact]
    public void Une_place_refusee_ne_laisse_rien_derriere_elle()
    {
        var slots = new ConnectionSlots();
        var limits = new RendezvousLimits { MaxConnectionsPerAddress = 10, MaxConnectionsPerPrefix = 1 };
        var (bucket, wide) = Of("2001:db8:7:1::1");

        Assert.True(slots.TryReserve(bucket, wide, limits));
        Assert.False(slots.TryReserve(bucket, wide, limits));
        Assert.Equal(1, slots.Total);
        Assert.Equal(1, slots.Held(bucket));

        slots.Release(bucket, wide);

        Assert.Equal(0, slots.Total);
        Assert.Equal(0, slots.Held(bucket));
        Assert.True(slots.TryReserve(bucket, wide, limits));
    }

    [Fact]
    public void Le_plafond_global_tient_compte_de_lunite_systemd()
    {
        // LimitNOFILE=65536 : assez de places pour des milliers de joueurs,
        // et de la marge pour le reste du processus.
        Assert.InRange(RendezvousLimits.Default.MaxConnections, 16_384, 49_152);
    }
}

/// <summary>Une session qui ne tient plus rien rend sa place.</summary>
public sealed class IdleSessionTests
{
    private static byte[] Box(byte seed) => Enumerable.Repeat(seed, RendezvousWire.MailboxAddressSize).ToArray();

    [Fact]
    public async Task Une_session_qui_ne_tient_rien_est_fermee_apres_le_delai()
    {
        await using var harness = await ServerHarness.StartAsync();

        var idle = await harness.ConnectAsync();
        await idle.SendAsync(RendezvousWire.Simple(RendezvousKind.DirectoryQuery));
        Assert.Equal(RendezvousKind.DirectoryList, (await idle.ReadFrameAsync())![0]);

        harness.Clock.Advance(RendezvousLimits.Default.IdleTimeout - TimeSpan.FromSeconds(1));
        harness.Server.Sweep();
        Assert.True(await idle.IsSilentAsync(TimeSpan.FromMilliseconds(200)));

        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        harness.Server.Sweep();

        Assert.True(await idle.IsClosedAsync(TimeSpan.FromSeconds(5)));
        await ServerHarness.WaitUntilAsync(() => harness.Server.Snapshot().Connections is 0);
        Assert.Equal(1, harness.Server.IdleClosed);
    }

    [Fact]
    public async Task Une_session_qui_tient_une_boite_reste_ouverte()
    {
        await using var harness = await ServerHarness.StartAsync();

        var holder = await harness.ConnectAsync();
        await holder.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        await ServerHarness.WaitUntilAsync(() => harness.Server.Snapshot().OpenMailboxes is 1);

        harness.Clock.Advance(TimeSpan.FromHours(3));
        harness.Server.Sweep();

        Assert.True(await holder.IsSilentAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(1, harness.Server.Snapshot().Connections);
    }
}
