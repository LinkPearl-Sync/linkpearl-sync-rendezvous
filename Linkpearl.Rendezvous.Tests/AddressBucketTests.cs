using System.Net;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Ce que le limiteur considère comme « la même adresse ».</summary>
public sealed class AddressBucketTests
{
    [Fact]
    public void En_ipv6_tout_un_64_compte_pour_une_adresse()
    {
        // Un abonné reçoit un /64 entier : compter par adresse lui donnerait
        // dix-huit trillions de limiteurs à lui seul.
        Assert.Equal(
            AddressBucket.Of(IPAddress.Parse("2001:db8:1:2:3:4:5:6")),
            AddressBucket.Of(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff")));

        Assert.NotEqual(
            AddressBucket.Of(IPAddress.Parse("2001:db8:1:2::1")),
            AddressBucket.Of(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void En_ipv4_ladresse_compte_seule()
    {
        Assert.Equal("198.51.100.7", AddressBucket.Of(IPAddress.Parse("198.51.100.7")));
        Assert.NotEqual(AddressBucket.Of(IPAddress.Parse("198.51.100.7")), AddressBucket.Of(IPAddress.Parse("198.51.100.8")));
    }

    [Fact]
    public void Une_ipv4_projetee_en_ipv6_compte_comme_lipv4()
    {
        // Une prise à double pile présente 198.51.100.7 en ::ffff:198.51.100.7,
        // et les deux doivent partager le même compteur.
        Assert.Equal(
            AddressBucket.Of(IPAddress.Parse("198.51.100.7")),
            AddressBucket.Of(IPAddress.Parse("::ffff:198.51.100.7")));
    }
}
