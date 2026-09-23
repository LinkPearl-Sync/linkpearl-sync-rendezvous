using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>La table des mondes : un nom ou un numéro, jamais une invention.</summary>
public sealed class WorldsTests
{
    [Fact]
    public void Les_identifiants_et_les_noms_sont_uniques()
    {
        Assert.Equal(Worlds.All.Count, Worlds.All.Select(world => world.Id).Distinct().Count());
        Assert.Equal(Worlds.All.Count, Worlds.All.Select(world => world.Name.ToLowerInvariant()).Distinct().Count());
        Assert.All(Worlds.All, world => Assert.True(world.Id > 0));
    }

    [Theory]
    [InlineData("Ragnarok", 97)]
    [InlineData("ragnarok", 97)]
    [InlineData(" Odin ", 66)]
    [InlineData("Ravana", 21)]
    [InlineData("Sargatanas", 99)]
    [InlineData("Tonberry", 72)]
    public void Un_nom_de_la_table_donne_son_identifiant(string name, int expected)
    {
        Assert.True(Worlds.TryResolve(name, out var id));
        Assert.Equal(expected, id);
    }

    [Fact]
    public void Un_identifiant_tape_passe_tel_quel_meme_hors_table()
    {
        // Un monde ouvert hier n'est pas encore dans la table ; qui en connaît
        // le numéro ne doit pas être bloqué par une liste en retard.
        Assert.True(Worlds.TryResolve("9999", out var id));
        Assert.Equal(9999, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("70000")]
    [InlineData("Nulle Part")]
    public void Ce_qui_nest_ni_un_nom_connu_ni_un_numero_valide_est_refuse(string input)
        => Assert.False(Worlds.TryResolve(input, out _));

    [Fact]
    public void Le_nom_dun_identifiant_se_retrouve()
    {
        Assert.Equal("Ragnarok", Worlds.NameOf(97));
        Assert.Null(Worlds.NameOf(9999));
    }
}
