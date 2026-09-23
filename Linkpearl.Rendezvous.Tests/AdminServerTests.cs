using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>
/// La console : ce qu'elle ouvre sans jeton, ce qu'elle refuse sans lui, et ce
/// qu'un clic y change sur le disque.
/// </summary>
public sealed class AdminServerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-admin-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _stopping = new();
    private readonly HttpClient _client = new();

    private PeerDirectory _directory = null!;
    private BanStore _bans = null!;
    private string _token = null!;
    private string _root = null!;
    private Task _running = null!;

    private string PeersPath => Path.Combine(_dir, "peers.txt");
    private string PendingPath => Path.Combine(_dir, "pending.txt");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);

        _directory = new PeerDirectory(PeersPath, PendingPath);
        _bans = new BanStore(Path.Combine(_dir, "bans.json"));
        _token = AdminToken.LoadOrCreate(Path.Combine(_dir, "admin.token"));

        var port = FreePort();
        _root = $"http://127.0.0.1:{port}";

        var service = new RendezvousServer(47900, _directory, RendezvousLimits.Default, new ManualClock());
        _running = new AdminServer(localOnly: true, port, 47900, _token, service, _directory, _bans, new ManualClock())
            .RunAsync(_stopping.Token);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _stopping.CancelAsync();

        try
        {
            await _running;
        }
        catch (Exception)
        {
            // L'arrêt ferme l'écouteur sous les pieds de l'accepteur.
        }

        _client.Dispose();
        _stopping.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task La_page_elle_meme_exige_le_jeton()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_root}/");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Sans cet en-tête, le navigateur montre une page d'erreur au lieu de
        // demander le mot de passe, et la console devient inatteignable.
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Un_appel_json_refuse_ne_declenche_pas_la_boite_de_dialogue()
    {
        // Avec un défi, le navigateur l'ouvrirait au milieu d'un
        // rafraîchissement automatique, sans que personne ne l'ait demandé.
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_root}/api/status");
        request.Headers.Add("Sec-Fetch-Mode", "cors");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task La_page_souvre_avec_le_jeton()
    {
        var response = await SendAsync(HttpMethod.Get, "/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("service de rendez-vous", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Le_navigateur_est_accepte_en_authentification_basique()
    {
        // C'est ce que renvoie un navigateur après sa boîte de dialogue :
        // l'identifiant ne compte pas, il n'y a qu'un secret.
        var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes($"peu importe:{_token}"));

        var request = new HttpRequestMessage(HttpMethod.Get, $"{_root}/api/status")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Basic", pair) },
        };

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Une_authentification_basique_fautive_est_refusee()
    {
        var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:mauvais"));

        var request = new HttpRequestMessage(HttpMethod.Get, $"{_root}/api/status")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Basic", pair) },
        };

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Sans_jeton_un_point_protege_rend_401_et_ne_revele_rien()
    {
        var response = await _client.GetAsync($"{_root}/api/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(_token, body);
        Assert.DoesNotContain("peers", body);
    }

    [Fact]
    public async Task Un_mauvais_jeton_est_refuse()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/status", token: new string('0', 64));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task La_liste_se_telecharge_sans_jeton_et_se_relit()
    {
        _bans.Add("Nom Fictif", 42, "contenu illegal");

        var response = await _client.GetAsync($"{_root}/api/bans");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(BanList.TryParse(await response.Content.ReadAsStringAsync(), out var list, out var why), why);
        Assert.True(list!.Contains("Nom Fictif", 42));
    }

    [Fact]
    public async Task Letat_porte_les_compteurs_et_lannuaire()
    {
        File.WriteAllText(PeersPath, "rdv.exemple.ch  Chez l'exemple\n");

        var response = await SendAsync(HttpMethod.Get, "/api/status");
        var state = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(47900, state["port"]!.GetValue<int>());
        Assert.Equal(0, state["openMailboxes"]!.GetValue<int>());
        Assert.Equal("rdv.exemple.ch", state["known"]![0]!["address"]!.GetValue<string>());
    }

    [Fact]
    public async Task Approuver_une_candidature_la_deplace_vers_les_pairs()
    {
        _directory.Submit("1.2.3.4", new DirectoryEntry("rdv.candidat.ch", "Candidat"));

        var response = await SendAsync(
            HttpMethod.Post, "/api/peers", body: """{"address":"rdv.candidat.ch"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_directory.Known(), entry => entry.Address == "rdv.candidat.ch");
        Assert.Empty(_directory.Pending());
        Assert.Contains("rdv.candidat.ch", File.ReadAllText(PeersPath));
    }

    [Fact]
    public async Task Approuver_ce_qui_nexiste_pas_rend_404()
    {
        var response = await SendAsync(
            HttpMethod.Post, "/api/peers", body: """{"address":"rdv.inconnu.ch"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retirer_vaut_pour_un_candidat_comme_pour_un_connu()
    {
        _directory.Submit("1.2.3.4", new DirectoryEntry("rdv.candidat.ch", "Candidat"));
        File.WriteAllText(PeersPath, "rdv.connu.ch  Connu\n");

        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Delete, "/api/peers", body: """{"address":"rdv.candidat.ch"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Delete, "/api/peers", body: """{"address":"rdv.connu.ch"}""")).StatusCode);

        Assert.Empty(_directory.Pending());
        Assert.Empty(_directory.Known());
    }

    [Fact]
    public async Task Un_bannissement_sajoute_puis_se_retire_depuis_la_console()
    {
        var added = await SendAsync(
            HttpMethod.Post, "/api/bans",
            body: """{"name":"Nom Fictif","world":42,"reason":"contenu illegal"}""");

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.True(_bans.Current().Contains("Nom Fictif", 42));

        var hash = JsonNode.Parse(await added.Content.ReadAsStringAsync())!["hash"]!.GetValue<string>();

        // Le même personnage une seconde fois n'ajoute rien, et le dit.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(
            HttpMethod.Post, "/api/bans",
            body: """{"name":"Nom Fictif","world":42,"reason":"encore"}""")).StatusCode);

        var removed = await SendAsync(HttpMethod.Delete, "/api/bans", body: $$"""{"hash":"{{hash}}"}""");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.False(_bans.Current().Contains("Nom Fictif", 42));
    }

    [Fact]
    public async Task Un_bannissement_sans_nom_est_refuse()
    {
        var response = await SendAsync(
            HttpMethod.Post, "/api/bans", body: """{"name":"   ","world":42,"reason":"x"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_bans.Current().Entries);
    }

    [Fact]
    public async Task Un_proxy_inverse_qui_passe_son_propre_nom_est_servi()
    {
        // Le déploiement prévu est derrière un proxy, qui passe le nom public.
        // Lié à 127.0.0.1, HttpListener apparie ses préfixes sur l'en-tête Host
        // et rendait 404 à tout proxy, et même à « localhost ».
        foreach (var host in new[] { "linkpearl.exemple.ch", "localhost", "127.0.0.1" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_root}/api/bans");
            request.Headers.Host = host;

            var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Un_chemin_inconnu_rend_404()
        => Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/api/rien")).StatusCode);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? body = null, string? token = null)
    {
        var request = new HttpRequestMessage(method, $"{_root}{path}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token ?? _token) },
        };

        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return await _client.SendAsync(request);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.20", false)]
    [InlineData("::ffff:192.168.1.20", false)]
    public void Seule_la_machine_locale_est_servie(string remote, bool served)
        => Assert.Equal(served, AdminServer.Serves(localOnly: true, IPAddress.Parse(remote)));

    [Fact]
    public void Une_adresse_inconnue_est_refusee()
        => Assert.False(AdminServer.Serves(localOnly: true, null));

    [Fact]
    public void Ouverte_a_tous_elle_ne_filtre_plus()
        => Assert.True(AdminServer.Serves(localOnly: false, IPAddress.Parse("192.168.1.20")));

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }
}

/// <summary>Le jeton d'administration : engendré une fois, puis relu.</summary>
public sealed class AdminTokenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-token-{Guid.NewGuid():N}");

    public AdminTokenTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Il_est_engendre_une_fois_puis_relu()
    {
        var path = Path.Combine(_dir, "admin.token");

        var first = AdminToken.LoadOrCreate(path);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, AdminToken.LoadOrCreate(path));
    }

    [Fact]
    public void Deux_services_nont_pas_le_meme_jeton()
        => Assert.NotEqual(
            AdminToken.LoadOrCreate(Path.Combine(_dir, "a.token")),
            AdminToken.LoadOrCreate(Path.Combine(_dir, "b.token")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("le-jeton")]
    [InlineData("Bearer ")]
    [InlineData("Bearer mauvais")]
    public void Un_en_tete_qui_ne_porte_pas_le_jeton_est_refuse(string? header)
        => Assert.False(AdminToken.Matches("le-jeton", header));

    [Fact]
    public void Len_tete_attendu_est_accepte()
        => Assert.True(AdminToken.Matches("le-jeton", "Bearer le-jeton"));
}
