using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Un client qui cesse de lire sans fermer ne retient personne.</summary>
public sealed class SlowReaderTests
{
    private static byte[] Box(byte seed) => Enumerable.Repeat(seed, RendezvousWire.MailboxAddressSize).ToArray();

    [Fact]
    public async Task Un_destinataire_qui_ne_lit_plus_est_coupe_et_rend_sa_boite()
    {
        await using var harness = await ServerHarness.StartAsync(RendezvousLimits.Default with
        {
            AnnouncementsPerMinute = 1_000_000,
            PeerSendTimeout = TimeSpan.FromMilliseconds(300),
        });

        // Il ouvre sa boîte, puis ne lit plus jamais rien.
        var stuck = await harness.ConnectAsync(receiveBuffer: 4096);
        await stuck.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        await ServerHarness.WaitUntilAsync(() => harness.Server.Snapshot().OpenMailboxes is 1);

        var sender = await harness.ConnectAsync();
        var payload = new byte[RendezvousWire.MaxDepositLength];
        var started = DateTime.UtcNow;

        // Les tampons du noyau absorbent d'abord quelques mégaoctets : on
        // dépose jusqu'à ce qu'ils soient pleins et que le délai tombe.
        while (harness.Server.Snapshot().OpenMailboxes is not 0)
        {
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "le destinataire n'a jamais été coupé");

            for (var i = 0; i < 200; i++)
                await sender.SendAsync(RendezvousWire.MailboxDeposit(Box(1), payload));

            await Task.Delay(10);
        }

        // Il a rendu sa place, et celui qui déposait est toujours servi.
        await ServerHarness.WaitUntilAsync(() => harness.Server.Snapshot().Connections is 1);
        Assert.True(PeerSession.SlowCuts >= 1);

        var other = await harness.ConnectAsync();
        await other.SendAsync(RendezvousWire.MailboxQuery([Box(1)]));
        Assert.Equal(RendezvousKind.MailboxPresence, (await other.ReadFrameAsync())![0]);
    }
}
