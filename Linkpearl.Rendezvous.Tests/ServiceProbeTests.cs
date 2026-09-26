using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>La sonde de l'autorité : une trame, une réponse, rien d'autre, et jamais vers le réseau local.</summary>
public sealed class ServiceProbeTests
{
    [Fact]
    public async Task Un_service_qui_repond_est_joint()
    {
        await using var harness = await ServerHarness.StartAsync();
        var probe = new ServiceProbe { AllowPrivate = true };

        var result = await probe.ProbeAsync(new RendezvousAddress("127.0.0.1", harness.Port), CancellationToken.None);

        Assert.True(result.Reached);
        Assert.Equal(IPAddress.Loopback, result.Address);
    }

    [Fact]
    public async Task Une_adresse_privee_n_est_jamais_sondee()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var result = await new ServiceProbe().ProbeAsync(new RendezvousAddress("127.0.0.1", port), CancellationToken.None);

            Assert.False(result.Reached);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Un_port_ferme_n_est_pas_joint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await new ServiceProbe { AllowPrivate = true }
            .ProbeAsync(new RendezvousAddress("127.0.0.1", port), CancellationToken.None);

        Assert.False(result.Reached);
    }

    [Fact]
    public async Task Un_service_muet_n_est_pas_joint_et_ne_retient_pas()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var probe = new ServiceProbe { AllowPrivate = true, Patience = TimeSpan.FromMilliseconds(300) };
            var watch = Stopwatch.StartNew();

            var result = await probe.ProbeAsync(new RendezvousAddress("127.0.0.1", port), CancellationToken.None);

            Assert.False(result.Reached);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Une_reponse_hors_protocole_n_est_pas_jointe()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var answering = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync();
                await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n"));
            });

            var result = await new ServiceProbe { AllowPrivate = true, Patience = TimeSpan.FromSeconds(2) }
                .ProbeAsync(new RendezvousAddress("127.0.0.1", port), CancellationToken.None);

            Assert.False(result.Reached);
            await answering;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("83.228.242.221", true)]
    [InlineData("2001:1600:18:202::1e4", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Seules_les_adresses_publiques_sont_sondables(string address, bool expected)
        => Assert.Equal(expected, ServiceProbe.IsPublic(IPAddress.Parse(address)));
}
