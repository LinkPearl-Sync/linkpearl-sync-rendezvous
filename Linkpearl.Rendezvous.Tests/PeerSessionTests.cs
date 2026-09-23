using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Une connexion cliente vue du serveur : ce qu'elle retient et ce qu'elle encaisse.</summary>
public sealed class PeerSessionTests
{
    /// <summary>Une paire de sockets connectées sur la boucle locale.</summary>
    private static async Task<(TcpClient Client, TcpClient Accepted)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = await listener.AcceptTcpClientAsync();
            return (client, accepted);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Ladresse_reste_lisible_apres_la_fermeture()
    {
        // Le journal et l'oubli des attentes lisent l'adresse après que la
        // socket a été fermée : la relire sur la socket lèverait alors.
        var (client, accepted) = await PairAsync();
        using var _ = client;

        var session = new PeerSession(accepted);
        var before = session.Address;

        session.Dispose();

        Assert.Equal("127.0.0.1", before);
        Assert.Equal(before, session.Address);
        Assert.Equal("127.0.0.1", session.Bucket);
    }

    [Fact]
    public async Task Le_keepalive_est_arme_sur_la_socket_acceptee()
    {
        var (client, accepted) = await PairAsync();
        using var _ = client;
        using var __ = accepted;

        PeerSession.Harden(accepted.Client, new RendezvousLimits
        {
            KeepAliveTime = TimeSpan.FromSeconds(60),
            KeepAliveInterval = TimeSpan.FromSeconds(10),
            KeepAliveRetryCount = 3,
        });

        Assert.NotEqual(0, (int)accepted.Client.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
        Assert.Equal(60, (int)accepted.Client.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
        Assert.Equal(10, (int)accepted.Client.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!);
        Assert.Equal(3, (int)accepted.Client.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!);
    }

    [Fact]
    public async Task Envoyer_a_une_session_fermee_rend_faux_sans_lever()
    {
        // Un partenaire parti entre son annonce et la nôtre ne doit pas faire
        // tomber notre propre session.
        var (client, accepted) = await PairAsync();
        using var _ = client;

        var session = new PeerSession(accepted);
        session.Dispose();

        Assert.False(await session.TrySendAsync(RendezvousWire.Simple(RendezvousKind.RelayReady), CancellationToken.None));
    }
}
