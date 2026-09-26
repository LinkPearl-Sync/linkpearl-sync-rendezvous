using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    private RendezvousServer _service = null!;
    private string _token = null!;
    private string _root = null!;
    private Task _running = null!;
    private Task _serving = null!;

    private string PeersPath => Path.Combine(_dir, "peers.txt");
    private string PendingPath => Path.Combine(_dir, "pending.txt");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);

        _directory = new PeerDirectory(PeersPath, PendingPath);
        _bans = new BanStore(Path.Combine(_dir, "bans.json"));
        _token = AdminToken.LoadOrCreate(Path.Combine(_dir, "admin.token"));

        // Le service tourne pour de vrai, sur un port éphémère : la santé
        // qu'expose la console est celle de ses boucles, pas un drapeau posé
        // par le test. Il est lancé avant de sonder un port pour la console :
        // dans l'autre ordre, le système pouvait lui donner le port que la
        // sonde venait de libérer, et l'écouteur HTTP tombait dessus.
        _service = new RendezvousServer(0, _directory, RendezvousLimits.Default, new ManualClock()) { Log = TextWriter.Null };
        _serving = _service.RunAsync(_stopping.Token);
        var servicePort = await _service.Listening.WaitAsync(TimeSpan.FromSeconds(5));

        // La réflexion démarre dans une tâche à part et entre dans sa boucle
        // quelques millisecondes après l'écoute : on l'attend, sans quoi la
        // santé lue par le premier appel dépendrait de l'ordonnanceur.
        for (var i = 0; i < 200 && _service.Healthy is false; i++)
            await Task.Delay(10);

        var port = FreePort();
        _root = $"http://127.0.0.1:{port}";

        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));

        var clock = new ManualClock();
        _authority = new AuthorityService(
            AuthorityLedger.Load(Path.Combine(_dir, "authority.json"), clock), new ScriptedProbe(),
            ECDsa.Create(ECCurve.NamedCurves.nistP256), _directory, clock)
        {
            Log = TextWriter.Null,
        };

        _running = new AdminServer(localOnly: true, port, servicePort, _token, _service, _directory, _bans, _settings, new ManualClock())
            {
                Log = _log,
                Authority = _authority,
            }
            .RunAsync(_stopping.Token);
    }

    private AuthorityService _authority = null!;

    private SettingsStore _settings = null!;
    private readonly StringWriter _log = new();

    public async Task DisposeAsync()
    {
        await _stopping.CancelAsync();

        foreach (var task in new[] { _running, _serving })
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                // L'arrêt ferme l'écouteur sous les pieds de l'accepteur.
            }
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
        Assert.Equal(_service.Port, state["port"]!.GetValue<int>());
        Assert.Equal(0, state["counters"]!["openMailboxes"]!.GetValue<int>());
        Assert.Equal("rdv.exemple.ch", state["known"]![0]!["address"]!.GetValue<string>());
    }

    [Fact]
    public async Task Letat_dit_la_version_la_memoire_et_la_sante_des_boucles()
    {
        var state = JsonNode.Parse(await (await SendAsync(HttpMethod.Get, "/api/status")).Content.ReadAsStringAsync())!;

        Assert.NotEqual("", state["version"]!.GetValue<string>());
        Assert.True(state["memory"]!["workingSetBytes"]!.GetValue<long>() > 0);
        Assert.True(state["memory"]!["gcHeapBytes"]!.GetValue<long>() > 0);
        Assert.True(state["health"]!["healthy"]!.GetValue<bool>());
        Assert.True(state["health"]!["accept"]!["alive"]!.GetValue<bool>());
        Assert.True(state["health"]!["reflect"]!["alive"]!.GetValue<bool>());
        Assert.NotNull(state["health"]!["accept"]!["lastTurn"]);
    }

    [Fact]
    public async Task Letat_ventile_les_refus_par_motif()
    {
        var state = JsonNode.Parse(await (await SendAsync(HttpMethod.Get, "/api/status")).Content.ReadAsStringAsync())!;

        foreach (var reason in new[] { "announce", "mailbox", "relay", "invitation", "connection" })
            Assert.Equal(0, state["refusals"]![reason]!.GetValue<long>());
    }

    [Theory]
    [InlineData("3m", 3)]
    [InlineData("1h", 60)]
    [InlineData("24h", 1440)]
    public async Task Lhistorique_rend_un_point_par_minute_sur_la_fenetre_demandee(string range, int expected)
    {
        _service.Sweep();

        var response = await SendAsync(HttpMethod.Get, $"/api/history?range={range}");
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var points = document["points"]!.AsArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, document["minutes"]!.GetValue<int>());
        Assert.Equal(expected, points.Count);
        Assert.Equal(0, points[^1]!["openMailboxes"]!.GetValue<int>());
        Assert.True(points[^1]!["at"]!.GetValue<long>() > points[0]!["at"]!.GetValue<long>() || expected is 1);
    }

    [Fact]
    public async Task Une_fenetre_inconnue_est_refusee()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/api/history?range=7d")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/api/history")).StatusCode);
    }

    [Fact]
    public async Task Lhistorique_exige_le_jeton()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"{_root}/api/history?range=1h")).StatusCode);

    [Fact]
    public async Task La_sante_se_lit_sans_jeton_et_ne_dit_rien_dautre()
    {
        // Un superviseur n'a pas de jeton, et une réponse publique ne doit
        // rien apprendre à qui la lit : un oui, c'est tout.
        var response = await _client.GetAsync($"{_root}/healthz");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"ok":true}""", body);
    }

    [Fact]
    public async Task Un_service_dont_les_boucles_ne_tournent_pas_rend_503()
    {
        // Un service construit et jamais lancé, ou dont la boucle est morte :
        // la console tient, mais elle doit dire que le service ne sert pas.
        var idle = new RendezvousServer(0, _directory, RendezvousLimits.Default, new ManualClock());
        var port = FreePort();

        using var stopping = new CancellationTokenSource();
        var running = new AdminServer(localOnly: true, port, 0, _token, idle, _directory, _bans, _settings, new ManualClock())
            .RunAsync(stopping.Token);

        var response = await _client.GetAsync($"http://127.0.0.1:{port}/healthz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"ok":false}""", await response.Content.ReadAsStringAsync());

        await stopping.CancelAsync();
        await running.ContinueWith(_ => { });
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
    public async Task Un_monde_se_donne_par_son_nom_ou_par_son_numero()
    {
        // La page envoie le nom choisi dans la liste ; la ligne de commande,
        // ou un monde trop récent pour la table, envoie le numéro.
        var byName = await SendAsync(
            HttpMethod.Post, "/api/bans", body: """{"name":"Nom Fictif","world":"Ragnarok","reason":"x"}""");

        Assert.Equal(HttpStatusCode.OK, byName.StatusCode);
        Assert.True(_bans.Current().Contains("Nom Fictif", 97));

        var byNumber = await SendAsync(
            HttpMethod.Post, "/api/bans", body: """{"name":"Autre Nom","world":"9999","reason":"x"}""");

        Assert.Equal(HttpStatusCode.OK, byNumber.StatusCode);
        Assert.True(_bans.Current().Contains("Autre Nom", 9999));

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(
            HttpMethod.Post, "/api/bans", body: """{"name":"Nom Fictif","world":"Nulle Part","reason":"x"}""")).StatusCode);
    }

    [Fact]
    public async Task La_table_des_mondes_se_lit_groupee_par_centre()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/worlds");
        var worlds = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["worlds"]!.AsArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Worlds.All.Count, worlds.Count);
        Assert.Contains(worlds, world => world!["name"]!.GetValue<string>() == "Ragnarok" && world["dataCenter"]!.GetValue<string>() == "Chaos");
    }

    [Fact]
    public async Task Verifier_dit_si_un_personnage_est_liste_sans_rien_ecrire()
    {
        _bans.Add("Nom Fictif", 97, "contenu illegal");
        var before = File.ReadAllText(Path.Combine(_dir, "bans.json"));

        var listed = await SendAsync(HttpMethod.Post, "/api/bans/verify", body: """{"name":"nom fictif","world":"Ragnarok"}""");
        var listedBody = JsonNode.Parse(await listed.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.True(listedBody["listed"]!.GetValue<bool>());
        Assert.Equal(97, listedBody["world"]!.GetValue<int>());

        var other = await SendAsync(HttpMethod.Post, "/api/bans/verify", body: """{"name":"Nom Fictif","world":"Odin"}""");

        Assert.False(JsonNode.Parse(await other.Content.ReadAsStringAsync())!["listed"]!.GetValue<bool>());
        Assert.Equal(before, File.ReadAllText(Path.Combine(_dir, "bans.json")));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Post, "/api/bans/verify", body: """{"name":"","world":"Odin"}""")).StatusCode);
    }

    [Fact]
    public async Task Lexport_est_un_telechargement_qui_se_relit()
    {
        _bans.Add("Nom Fictif", 97, "contenu illegal");

        var response = await SendAsync(HttpMethod.Get, "/api/bans/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("bans.json", response.Content.Headers.ContentDisposition.FileName);
        Assert.True(BanList.TryParse(await response.Content.ReadAsStringAsync(), out var list, out var why), why);
        Assert.True(list!.Contains("Nom Fictif", 97));
    }

    [Fact]
    public async Task Lexport_exige_le_jeton()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"{_root}/api/bans/export")).StatusCode);

    [Fact]
    public async Task Limport_fusionne_une_liste_sous_le_meme_sel()
    {
        _bans.Add("Nom Fictif", 97, "ici");

        var otherPath = Path.Combine(_dir, "autre.json");
        File.Copy(Path.Combine(_dir, "bans.json"), otherPath);
        var theirs = new BanStore(otherPath);
        theirs.Add("Autre Nom", 66, "ailleurs");

        var response = await SendAsync(HttpMethod.Post, "/api/bans/import", body: theirs.Json());
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, result["added"]!.GetValue<int>());
        Assert.Equal(2, result["total"]!.GetValue<int>());
        Assert.True(_bans.Current().Contains("Autre Nom", 66));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Limport_dune_liste_sous_un_autre_sel_rend_409_et_ne_change_rien()
    {
        _bans.Add("Nom Fictif", 97, "ici");
        var before = File.ReadAllText(Path.Combine(_dir, "bans.json"));

        var foreign = new BanStore(Path.Combine(_dir, "etranger.json"));
        foreign.Add("Autre Nom", 66, "ailleurs");

        var response = await SendAsync(HttpMethod.Post, "/api/bans/import", body: foreign.Json());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("sel", body);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_dir, "bans.json")));
    }

    [Fact]
    public async Task Limport_dune_liste_illisible_rend_400()
        => Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Post, "/api/bans/import", body: """{"version":99}""")).StatusCode);

    [Fact]
    public async Task Les_reglages_se_lisent_avec_leurs_bornes()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/settings");
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RendezvousLimits.Default.AnnouncementsPerMinute, document["values"]!["announcementsPerMinute"]!.GetValue<int>());
        Assert.True(document["values"]!["relayEnabled"]!.GetValue<bool>());
        Assert.Equal(1, document["bounds"]!["maxConnections"]!["min"]!.GetValue<int>());
        Assert.False(document["persisted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Un_reglage_change_sapplique_a_chaud_se_persiste_et_se_journalise_sans_adresse()
    {
        var response = await SendAsync(HttpMethod.Put, "/api/settings",
            body: """{"announcementsPerMinute": 120, "relayEnabled": false}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(120, _service.Limits.AnnouncementsPerMinute);
        Assert.False(_service.Limits.RelayEnabled);
        Assert.Equal(RendezvousLimits.Default.MaxConnections, _service.Limits.MaxConnections);

        // Relu au redémarrage : le fichier surcharge la ligne de commande.
        var reloaded = new SettingsStore(Path.Combine(_dir, "settings.json"))
            .Load(new RendezvousLimits { AnnouncementsPerMinute = 60 }, TextWriter.Null);

        Assert.Equal(120, reloaded.AnnouncementsPerMinute);
        Assert.False(reloaded.RelayEnabled);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        Assert.Contains("announcementsPerMinute 60 -> 120", _log.ToString());
        Assert.Contains("relayEnabled true -> false", _log.ToString());
        Assert.DoesNotContain("127.0.0.1", _log.ToString());
    }

    [Theory]
    [InlineData("""{"announcementsPerMinute": 0}""")]
    [InlineData("""{"maxConnections": -1}""")]
    [InlineData("""{"maxMailboxesPerSession": 100000}""")]
    [InlineData("""{"relayEnabled": "non"}""")]
    [InlineData("""{"inconnu": 3}""")]
    [InlineData("""[1, 2]""")]
    public async Task Un_reglage_hors_bornes_est_refuse_sans_rien_changer(string body)
    {
        var before = _service.Limits;

        var response = await SendAsync(HttpMethod.Put, "/api/settings", body: body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Same(before, _service.Limits);
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public async Task Un_reglage_venu_dun_autre_site_est_refuse()
    {
        var request = Request(HttpMethod.Put, "/api/settings", """{"announcementsPerMinute": 120}""");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(request)).StatusCode);
        Assert.Equal(RendezvousLimits.Default.AnnouncementsPerMinute, _service.Limits.AnnouncementsPerMinute);
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
    public async Task Une_mutation_venue_dun_autre_site_est_refusee()
    {
        // Le navigateur rejoue l'authentification basique sur toute requête
        // vers cette origine, y compris celles qu'un formulaire posé ailleurs
        // lui fait envoyer : sans ce refus, une page tierce pouvait bannir
        // quelqu'un à la place de l'opérateur connecté.
        _directory.Submit("1.2.3.4", new DirectoryEntry("rdv.candidat.ch", "Candidat"));

        var request = Request(HttpMethod.Post, "/api/peers", """{"address":"rdv.candidat.ch"}""");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(_directory.Pending());
    }

    [Fact]
    public async Task Une_mutation_du_meme_site_passe()
    {
        _directory.Submit("1.2.3.4", new DirectoryEntry("rdv.candidat.ch", "Candidat"));

        var request = Request(HttpMethod.Post, "/api/peers", """{"address":"rdv.candidat.ch"}""");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Une_mutation_qui_nest_pas_du_json_est_refusee()
    {
        // Un formulaire HTML ne sait envoyer que trois types de contenu, et
        // JSON n'en fait pas partie : l'exiger ferme la porte aux envois que
        // le navigateur fait sans demander de permission.
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_root}/api/bans")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _token) },
            Content = new StringContent("""{"name":"Nom Fictif","world":42,"reason":"x"}""", Encoding.UTF8, "text/plain"),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(_bans.Current().Entries);
    }

    [Fact]
    public async Task Une_ecriture_de_la_console_ne_laisse_pas_de_fichier_temporaire()
    {
        await SendAsync(HttpMethod.Post, "/api/bans", body: """{"name":"Nom Fictif","world":42,"reason":"x"}""");
        _directory.Submit("1.2.3.4", new DirectoryEntry("rdv.candidat.ch", "Candidat"));
        await SendAsync(HttpMethod.Post, "/api/peers", body: """{"address":"rdv.candidat.ch"}""");

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.True(_bans.Current().Contains("Nom Fictif", 42));
        Assert.Contains("rdv.candidat.ch", File.ReadAllText(PeersPath));
    }

    [Fact]
    public async Task Un_chemin_inconnu_rend_404()
        => Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/api/rien")).StatusCode);

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? body = null, string? token = null)
        => _client.SendAsync(Request(method, path, body, token));

    private HttpRequestMessage Request(HttpMethod method, string path, string? body = null, string? token = null)
    {
        var request = new HttpRequestMessage(method, $"{_root}{path}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token ?? _token) },
        };

        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return request;
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

    [Fact]
    public async Task L_etat_montre_la_cle_de_l_autorite()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains(Convert.ToHexStringLower(_authority.PublicPoint), body);
    }

    [Fact]
    public async Task Ecarter_un_service_suivi_le_retire_du_cercle()
    {
        _authority.Ledger.Track(new DirectoryEntry("rdv.suspect.ch", "Suspect"));

        var response = await SendAsync(HttpMethod.Post, "/api/authority/veto", body: """{"address":"rdv.suspect.ch"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ServiceStanding.Vetoed, _authority.Ledger.Snapshot().Single().Standing);
    }

    [Fact]
    public async Task Ecarter_un_service_inconnu_rend_404()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/authority/veto", body: """{"address":"rdv.inconnu.ch"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retablir_un_service_ecarte_le_rend_candidat()
    {
        _authority.Ledger.Track(new DirectoryEntry("rdv.suspect.ch", "Suspect"));
        _authority.Ledger.Veto("rdv.suspect.ch");

        var response = await SendAsync(HttpMethod.Delete, "/api/authority/veto", body: """{"address":"rdv.suspect.ch"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ServiceStanding.Candidate, _authority.Ledger.Snapshot().Single().Standing);
    }

    [Fact]
    public async Task Ecarter_sans_jeton_est_refuse()
    {
        var response = await SendAsync(
            HttpMethod.Post, "/api/authority/veto", body: """{"address":"rdv.suspect.ch"}""", token: new string('0', 64));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_authority.Ledger.Snapshot());
    }

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

/// <summary>Ce que le jeton laisse voir, sur le disque et dans le journal.</summary>
public sealed class AdminTokenExposureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-token-{Guid.NewGuid():N}");

    public AdminTokenExposureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Le_journal_ne_porte_que_le_chemin_du_jeton()
    {
        // Un journal est relu par des yeux qui n'ont pas à ouvrir la console,
        // et copié dans des rapports : le jeton se lit dans son fichier.
        var path = Path.Combine(_dir, "admin.token");
        var log = new StringWriter();

        var token = AdminToken.LoadOrCreate(path, log);

        Assert.Contains(path, log.ToString());
        Assert.DoesNotContain(token, log.ToString());
    }

    [Fact]
    public void Le_fichier_du_jeton_nest_lisible_que_par_son_proprietaire()
    {
        // Sur un VPS partagé, un jeton lisible par tout le monde vaut un jeton
        // public. Le mode est donné à la création, pas posé après coup : entre
        // les deux, le fichier aurait existé un instant avec le masque par défaut.
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(_dir, "admin.token");

        AdminToken.LoadOrCreate(path, TextWriter.Null);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
