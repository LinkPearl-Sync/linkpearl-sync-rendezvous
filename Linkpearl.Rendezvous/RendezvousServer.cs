using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Le service de rendez-vous.
/// </summary>
/// <remarks>
/// Il fait trois choses, et rien d'autre :
///
/// <list type="number">
/// <item>il renvoie à un client l'adresse d'où il le voit, ce qui lui permet de
/// découvrir son adresse publique ;</item>
/// <item>il met en relation deux clients qui présentent le même jeton opaque, en
/// leur échangeant des blocs qu'il ne sait pas lire ;</item>
/// <item>il relaie des octets chiffrés quand la connexion directe échoue.</item>
/// </list>
///
/// Ce qu'il apprend, à écrire tel quel dans le modèle de menace : des adresses
/// IP, des jetons opaques qui tournent toutes les dix minutes, et le fait que
/// deux adresses partagent un jeton dans une fenêtre. Ni clé publique, ni nom de
/// personnage, ni manifeste, ni fichier. Un observateur ne peut pas relier deux
/// fenêtres entre elles.
///
/// Rien de ce qu'il fait n'est persisté : jetons, attentes, boîtes et
/// invitations vivent en mémoire et disparaissent avec le processus. Ce que
/// l'opérateur configure vit sur le disque, à côté : l'annuaire
/// (<see cref="PeerDirectory"/>), la liste de bannissement
/// (<see cref="BanStore"/>) et le jeton de la console (<see cref="AdminToken"/>).
/// </remarks>
public sealed class RendezvousServer(
    int requestedPort, PeerDirectory directory, RendezvousLimits limits, IClock clock, bool verbose = false)
{
    private RendezvousLimits _limits = limits;

    /// <summary>Où le service écrit ce qu'il fait.</summary>
    public TextWriter Log { get; init; } = Console.Out;

    /// <summary>La liste que le service publie ; nulle, il répond qu'il n'en a pas.</summary>
    public BanStore? Bans { get; init; }

    /// <summary>La liste signée à servir, présente seulement sur une autorité.</summary>
    public IConsensusSource? Consensus { get; init; }

    /// <summary>
    /// Ce qui reçoit une candidature et l'adresse qui l'a soumise, sur une autorité.
    /// </summary>
    public Action<DirectoryEntry, string>? Candidacy { get; init; }

    /// <summary>
    /// Une ligne de journal, avec ou sans son détail.
    /// </summary>
    /// <remarks>
    /// Par défaut, le journal dit ce qui se passe et jamais à qui : un journal
    /// est un fichier qui reste, relu et copié, et y écrire des adresses et
    /// des fragments de jetons ferait du service l'index qu'il promet de ne
    /// pas tenir. Le mode verbeux rend le détail pour diagnostiquer une
    /// soirée, et se coupe ensuite. Le ticket d'invitation n'apparaît dans
    /// aucun des deux : c'est un secret qui se retire, pas un identifiant.
    /// </remarks>
    private void Note(string what, string detail) => Log.WriteLine(verbose ? $"{what} : {detail}" : what);

    /// <summary>
    /// Les plafonds en vigueur, remplaçables d'un bloc.
    /// </summary>
    /// <remarks>
    /// Chaque chemin de code en prend une copie locale au début de son travail :
    /// un réglage changé depuis la console ne s'applique donc jamais à moitié.
    /// </remarks>
    public RendezvousLimits Limits
    {
        get => Volatile.Read(ref _limits);
        set => Volatile.Write(ref _limits, value);
    }

    /// <summary>Le port réellement lié, connu une fois l'écoute ouverte.</summary>
    /// <remarks>
    /// Un port zéro laisse le système en choisir un libre, ce qui permet à des
    /// tests de faire tourner plusieurs services en parallèle sans se marcher
    /// dessus. Ce port sert au TCP et à l'UDP, comme le port configuré.
    /// </remarks>
    public int Port { get; private set; }

    private readonly TaskCompletionSource<int> _listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Achevée quand le service accepte des connexions, avec le port lié.</summary>
    public Task<int> Listening => _listening.Task;

    /// <summary>
    /// Ce que le service a fait depuis son démarrage.
    /// </summary>
    /// <remarks>
    /// Tenu même sans personne pour le lire : une console d'administration
    /// viendra plus tard, et reconstituer ces compteurs après coup demanderait
    /// de retoucher chaque chemin de code. Aucune trame ne les expose.
    /// </remarks>
    public sealed record Counters(
        int OpenMailboxes, int PendingAnnouncements, int RelayWaiting, int ActiveRelays, int PendingInvitations,
        long Matches, long Relays, long RelayedBytes,
        int Connections, long RefusedConnections, long RateRefusals, int TrackedAddresses,
        int KnownPeers, int PendingSubmissions,
        Refusals Refusals);

    /// <summary>
    /// Les refus du limiteur, ventilés par ce qui était demandé.
    /// </summary>
    /// <remarks>
    /// Un total seul ne dit pas si c'est un client qui annonce en boucle ou
    /// quelqu'un qui parcourt l'espace des tickets d'invitation : la réponse
    /// de l'opérateur n'est pas la même. Les connexions refusées viennent des
    /// plafonds et non du limiteur, mais elles se lisent au même endroit.
    /// </remarks>
    public sealed record Refusals(long Announce, long Mailbox, long Relay, long Invitation, long Connection);

    public Counters Snapshot()
    {
        int invitations;

        lock (_invitationGate)
            invitations = _invitations.Count;

        int mailboxes;

        lock (_mailboxGate)
            mailboxes = _mailboxes.Count;

        var refusedConnections = Interlocked.Read(ref _refusedConnections);

        return new(
            mailboxes, _waiting.Count, _relayWaiting.Count, Volatile.Read(ref _activeRelays), invitations,
            Interlocked.Read(ref _matched), Interlocked.Read(ref _relayed), PeerSession.TotalRelayedBytes,
            Volatile.Read(ref _connections), refusedConnections,
            Interlocked.Read(ref _rateRefusals), _rate.Count,
            directory.Known().Count, directory.Pending().Count,
            new Refusals(
                Interlocked.Read(ref _refusedAnnounces), Interlocked.Read(ref _refusedMailboxes),
                Interlocked.Read(ref _refusedRelays), Interlocked.Read(ref _refusedInvitations),
                refusedConnections));
    }

    private long _rateRefusals;
    private long _refusedAnnounces;
    private long _refusedMailboxes;
    private long _refusedRelays;
    private long _refusedInvitations;

    private int _connections;
    private int _activeRelays;
    private long _refusedConnections;

    /// <summary>Vingt-quatre heures de trafic, en mémoire seulement.</summary>
    public History History { get; } = new();

    /// <summary>La boucle qui accepte les connexions TCP.</summary>
    public LoopHealth AcceptLoop { get; } = new(clock);

    /// <summary>La boucle qui répond aux sondes UDP de réflexion.</summary>
    public LoopHealth ReflectLoop { get; } = new(clock);

    /// <summary>
    /// Vrai quand les deux boucles tournent.
    /// </summary>
    /// <remarks>
    /// C'est ce qu'un superviseur doit regarder : un processus debout dont une
    /// boucle est morte est pire qu'un processus tombé, parce que personne ne
    /// le redémarre.
    /// </remarks>
    public bool Healthy => AcceptLoop.Alive && ReflectLoop.Alive;

    /// <summary>Connexions tenues par seau d'adresses, pour le plafond par adresse.</summary>
    private readonly ConcurrentDictionary<string, int> _connectionsPerBucket = new(StringComparer.Ordinal);

    private sealed class Waiting
    {
        public required PeerSession Session { get; init; }
        public required byte[] SealedCandidates { get; init; }
        public required DateTimeOffset Since { get; init; }
    }

    private readonly ConcurrentDictionary<string, Waiting> _waiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (PeerSession Session, DateTimeOffset Since)> _relayWaiting =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Window)> _rate = new(StringComparer.Ordinal);

    /// <summary>
    /// Les invitations déposées, à usage unique.
    /// </summary>
    /// <remarks>
    /// Le serveur lit cette charge, et pourrait donc la remplacer. C'est le prix
    /// d'un ticket de douze caractères, qui ne peut pas porter une empreinte de
    /// clé. La substitution se détecte après coup par la comparaison des six
    /// mots du handshake ; une fois la clé épinglée, le serveur n'a plus aucun
    /// pouvoir sur cette paire.
    /// </remarks>
    private sealed record Invitation(byte[] Payload, DateTimeOffset Expiry, string Bucket);

    // Un verrou et non un dictionnaire concurrent : le plafond par adresse
    // demande un compte tenu à jour à chaque dépôt et retrait, et deux
    // structures concurrentes ne se mettent pas d'accord sans lui. Les
    // invitations sont rares, le verrou ne se voit pas.
    private readonly Lock _invitationGate = new();
    private readonly Dictionary<string, Invitation> _invitations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _invitationsPerBucket = new(StringComparer.Ordinal);

    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(24);

    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Les boîtes ouvertes, et les sessions qui les tiennent.
    /// </summary>
    /// <remarks>
    /// Une adresse de boîte dérive du nom de personnage, donc ce registre dit de
    /// fait qui est en ligne. C'est le prix assumé de la découvrabilité : un
    /// inconnu ne peut pas reconnaître quelqu'un sans que le serveur le puisse
    /// aussi. Ce registre ne quitte jamais la mémoire, et l'adresse tourne
    /// toutes les trente minutes, ce qui empêche de relier deux périodes.
    ///
    /// Plusieurs sessions par boîte : deux clients sur la même machine, ou une
    /// reconnexion dont l'ancienne session n'est pas encore tombée. Une seule
    /// session par boîte faisait que la seconde ouverture écrasait la
    /// première, qui ne recevait plus rien sans le savoir, et que le départ de
    /// l'une fermait la boîte de l'autre.
    /// </remarks>
    private readonly Lock _mailboxGate = new();
    private readonly Dictionary<string, HashSet<PeerSession>> _mailboxes = new(StringComparer.Ordinal);

    // Incrémentés depuis la boucle de service de n'importe quelle session :
    // un « ++ » y perdrait des unités.
    private long _matched;
    private long _relayed;

    public long Matched => Interlocked.Read(ref _matched);

    public long Relayed => Interlocked.Read(ref _relayed);

    public async Task RunAsync(CancellationToken ct)
    {
        TcpListener listener;
        UdpClient reflection;

        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, requestedPort);
            listener.Server.DualMode = true;
            listener.Start();

            // Le port lié et non le port demandé : avec zéro, c'est le système
            // qui l'a choisi, et l'UDP doit suivre le TCP.
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;

            reflection = new UdpClient(AddressFamily.InterNetworkV6);
            reflection.Client.DualMode = true;
            reflection.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, Port));
        }
        catch (Exception e)
        {
            // Sans cela, qui attend l'écoute attendrait pour toujours.
            _listening.TrySetException(e);
            throw;
        }

        Log.WriteLine($"Rendez-vous en écoute sur le port {Port}, TCP et UDP, IPv4 et IPv6.");
        Log.WriteLine(verbose
            ? "Journal détaillé : adresses et fragments de jetons inclus. À couper une fois le diagnostic fait."
            : "Journal sans adresse ni fragment de jeton. --verbose pour le détail.");
        Log.WriteLine();

        _listening.TrySetResult(Port);

        try
        {
            _ = Task.Run(() => ReflectAsync(reflection, ct), ct);
            _ = Task.Run(() => ExpireAsync(ct), ct);

            AcceptLoop.Enter();

            while (ct.IsCancellationRequested is false)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (ct.IsCancellationRequested is false)
                {
                    // Table des descripteurs pleine, ou connexion réinitialisée
                    // avant d'être prise : une boucle qui mourrait ici laisserait
                    // le service sourd tout en paraissant vivant. On souffle, le
                    // temps qu'une session se termine, puis on reprend.
                    Log.WriteLine($"Acceptation en échec ({e.GetType().Name}), reprise dans 100 ms.");
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    continue;
                }

                AcceptLoop.Turn();
                Admit(client, ct);
            }
        }
        finally
        {
            // Le drapeau tombe quelle que soit la sortie : c'est ce que
            // /healthz regarde, et une sortie par exception est précisément
            // le cas où il doit tomber.
            AcceptLoop.Exit();

            // Rendre les ports tout de suite : un test qui enchaîne des
            // services, ou un redémarrage rapide, ne doit pas tomber sur un
            // port encore tenu par l'instance précédente.
            listener.Stop();
            reflection.Dispose();
        }
    }

    /// <summary>Renvoie à un client l'adresse d'où on le voit.</summary>
    private async Task ReflectAsync(UdpClient reflection, CancellationToken ct)
    {
        ReflectLoop.Enter();

        try
        {
            while (ct.IsCancellationRequested is false)
            {
                try
                {
                    var received = await reflection.ReceiveAsync(ct).ConfigureAwait(false);

                    ReflectLoop.Turn();

                    if (received.Buffer.Length == 0 || received.Buffer[0] != RendezvousKind.Reflect)
                        continue;

                    var from = Normalize(received.RemoteEndPoint);
                    var address = from.Address.GetAddressBytes();

                    var reply = new byte[2 + address.Length + 2];
                    reply[0] = RendezvousKind.Reflected;
                    reply[1] = (byte)address.Length;
                    address.CopyTo(reply.AsSpan(2));
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2 + address.Length), (ushort)from.Port);

                    // L'envoi est dans le même filet que la réception : une
                    // destination injoignable fait lever l'envoi sur certaines
                    // plateformes, et une seule exception tuait la réflexion pour
                    // toujours, sans que le service paraisse mort.
                    await reflection.SendAsync(reply, received.RemoteEndPoint, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Datagramme illisible ou destination injoignable : au suivant.
                }
            }
        }
        finally
        {
            ReflectLoop.Exit();
        }
    }

    /// <summary>
    /// Garde une connexion si les plafonds le permettent, la ferme sinon.
    /// </summary>
    /// <remarks>
    /// Fermée sans un mot : écrire vers une socket qu'on refuse, c'est déjà
    /// lui consacrer du travail, et c'est précisément ce qu'un flot de
    /// connexions cherche à obtenir.
    /// </remarks>
    private void Admit(TcpClient client, CancellationToken ct)
    {
        var session = new PeerSession(client);

        if (TryReserve(session.Bucket) is false)
        {
            Interlocked.Increment(ref _refusedConnections);
            session.Dispose();
            return;
        }

        // Appelée et non confiée à Task.Run : une tâche annulée avant d'avoir
        // démarré ne libérerait ni la place ni la socket.
        _ = ServeAsync(session, ct);
    }

    private bool TryReserve(string bucket)
    {
        var limits = Limits;

        if (Interlocked.Increment(ref _connections) > limits.MaxConnections)
        {
            Interlocked.Decrement(ref _connections);
            return false;
        }

        if (_connectionsPerBucket.AddOrUpdate(bucket, 1, (_, held) => held + 1) > limits.MaxConnectionsPerAddress)
        {
            Release(bucket);
            return false;
        }

        return true;
    }

    private void Release(string bucket)
    {
        Interlocked.Decrement(ref _connections);

        // L'entrée disparaît à zéro : sans cela, le dictionnaire garderait une
        // trace de chaque adresse jamais vue.
        while (_connectionsPerBucket.TryGetValue(bucket, out var held))
        {
            if (held <= 1
                ? _connectionsPerBucket.TryRemove(new KeyValuePair<string, int>(bucket, held))
                : _connectionsPerBucket.TryUpdate(bucket, held - 1, held))
                return;
        }
    }

    private async Task ServeAsync(PeerSession session, CancellationToken ct)
    {
        try
        {
            PeerSession.Harden(session.Socket, Limits);

            var first = true;

            while (ct.IsCancellationRequested is false)
            {
                byte[]? frame;

                if (first)
                {
                    // La première trame a un délai, les suivantes n'en ont pas :
                    // une boîte reste ouverte des heures sans rien dire, et
                    // c'est le keepalive qui constate sa mort.
                    using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    patience.CancelAfter(Limits.FirstFrameTimeout);

                    try
                    {
                        frame = await session.ReadFrameAsync(patience.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested is false)
                    {
                        return;
                    }

                    first = false;
                }
                else
                {
                    frame = await session.ReadFrameAsync(ct).ConfigureAwait(false);
                }

                if (frame is null)
                    return;

                var handled = frame[0] switch
                {
                    RendezvousKind.Announce => await HandleAnnounceAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.RelayOpen => await HandleRelayAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.TicketRegister => await HandleRegisterTicketAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.TicketRedeem => await HandleRedeemTicketAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.MailboxOpen => await HandleMailboxOpenAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.MailboxQuery => await HandleMailboxQueryAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.MailboxDeposit => await HandleMailboxDepositAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.DirectoryQuery => await HandleDirectoryQueryAsync(session, ct).ConfigureAwait(false),
                    RendezvousKind.DirectorySubmit => HandleDirectorySubmit(session, frame),
                    RendezvousKind.BanListQuery => await HandleBanListQueryAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.ConsensusQuery => await HandleConsensusQueryAsync(session, frame, ct).ConfigureAwait(false),
            RendezvousKind.NetworkStatusQuery => await HandleNetworkStatusQueryAsync(session, frame, ct).ConfigureAwait(false),
                    _ => false,
                };

                if (handled is false)
                {
                    await session.SendAsync(RendezvousWire.Error("trame inattendue"), ct).ConfigureAwait(false);
                    return;
                }

                if (session.IsParked)
                {
                    // Garée : le pontage lira sa socket, donc cette boucle doit
                    // cesser de la lire. Deux lecteurs concurrents sur le même
                    // flux se volent les trames.
                    await session.WaitUntilRelayFinishedAsync(ct).ConfigureAwait(false);
                    return;
                }

                if (session.IsRelaying)
                    return;   // le pontage s'est déroulé dans cet appel
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Déconnexion ordinaire : rien à journaliser.
        }
        catch (Exception e)
        {
            // Tout le reste est une faute du service, pas du client, et une
            // faute qu'on ne voit pas ne se corrige jamais.
            Log.WriteLine($"Session en échec ({e.GetType().Name}) : {e.Message}");
        }
        finally
        {
            Forget(session);
            Release(session.Bucket);
            session.Dispose();
        }
    }

    /// <summary>Publie les services connus. L'annuaire informe, il ne décide pas.</summary>
    private async Task<bool> HandleDirectoryQueryAsync(PeerSession session, CancellationToken ct)
    {
        await session.SendAsync(RendezvousWire.Directory(directory.Known()), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reçoit la candidature d'un service.
    /// </summary>
    /// <remarks>
    /// Aucune réponse n'est faite, et c'est délibéré : le candidat n'a pas à
    /// savoir s'il a été retenu, et une réponse ferait de cette trame un moyen
    /// de sonder la file d'attente.
    /// </remarks>
    private bool HandleDirectorySubmit(PeerSession session, byte[] frame)
    {
        if (RendezvousWire.TryReadDirectory(frame, out var submitted, out _) && submitted.Count is 1)
        {
            directory.Submit(session.Bucket, submitted[0]);
            Candidacy?.Invoke(submitted[0], session.Bucket);
        }

        return true;
    }

    /// <summary>
    /// Sert une page de la liste de bannissement.
    /// </summary>
    /// <remarks>
    /// La même que <c>GET /api/bans</c>, découpée : la console n'écoute qu'en
    /// local, et c'est ici que les clients la trouvent. Une page hors de la
    /// liste n'est pas une faute : la liste a pu raccourcir entre deux pages.
    /// </remarks>
    private async Task<bool> HandleBanListQueryAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (RendezvousWire.TryReadBanListQuery(frame, out var page) is false)
        {
            await session.SendAsync(RendezvousWire.Error("demande de liste malformée"), ct).ConfigureAwait(false);
            return false;
        }

        if (++session.BanPagesServed > RendezvousWire.MaxBanListPages)
        {
            await session.SendAsync(RendezvousWire.Error("trop de pages demandées"), ct).ConfigureAwait(false);
            return false;
        }

        if (Bans is null)
        {
            await session.SendAsync(RendezvousWire.Error("liste de bannissement indisponible"), ct).ConfigureAwait(false);
            return true;
        }

        var (json, pages) = Bans.Page(page, RendezvousWire.BanListPageEntries);

        await session.SendAsync(
            json is null ? RendezvousWire.Error("page hors de la liste") : RendezvousWire.BanListData(page, pages, json),
            ct).ConfigureAwait(false);

        return true;
    }

    private Task<bool> HandleConsensusQueryAsync(PeerSession session, byte[] frame, CancellationToken ct)
        => RendezvousWire.TryReadConsensusQuery(frame, out var page)
            ? ServeChunksAsync(session, page, Consensus?.Document, "ce service ne publie pas de liste signée", RendezvousWire.ConsensusPage, ct)
            : RefuseAsync(session, "demande de liste signée malformée", ct);

    private Task<bool> HandleNetworkStatusQueryAsync(PeerSession session, byte[] frame, CancellationToken ct)
        => RendezvousWire.TryReadNetworkStatusQuery(frame, out var page)
            ? ServeChunksAsync(session, page, Consensus?.Status, "ce service ne publie pas l'état du réseau", RendezvousWire.NetworkStatusPage, ct)
            : RefuseAsync(session, "demande d'état du réseau malformée", ct);

    private static async Task<bool> RefuseAsync(PeerSession session, string why, CancellationToken ct)
    {
        await session.SendAsync(RendezvousWire.Error(why), ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>Sert une page d'un document découpé en tranches : liste signée ou état du réseau.</summary>
    /// <remarks>
    /// Les deux documents partagent un même plafond de pages par connexion :
    /// trois octets de demande pour 32 Kio de réponse feraient sinon du
    /// service un amplificateur.
    ///
    /// Le document est lu une fois par page : si l'autorité le réémet entre
    /// deux pages, le client recolle deux versions, et la signature ou la
    /// lecture échoue ; il garde alors ce qu'il avait. Rien à verrouiller ici.
    /// </remarks>
    private static async Task<bool> ServeChunksAsync(
        PeerSession session, int page, byte[]? document, string unavailable,
        Func<int, int, ReadOnlySpan<byte>, byte[]> makePage, CancellationToken ct)
    {
        if (++session.ConsensusPagesServed > RendezvousWire.MaxConsensusPages)
            return await RefuseAsync(session, "trop de pages demandées", ct).ConfigureAwait(false);

        if (document is null)
        {
            await session.SendAsync(RendezvousWire.Error(unavailable), ct).ConfigureAwait(false);
            return true;
        }

        var size = RendezvousWire.ConsensusPageBytes;
        var pages = Math.Max(1, (document.Length + size - 1) / size);

        if (page >= pages)
        {
            await session.SendAsync(RendezvousWire.Error("page hors du document"), ct).ConfigureAwait(false);
            return true;
        }

        var frame = makePage(page, pages, document.AsSpan(page * size, Math.Min(size, document.Length - page * size)));
        await session.SendAsync(frame, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleAnnounceAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (RateExceeded(session.Bucket, ref _refusedAnnounces))
        {
            await session.SendAsync(RendezvousWire.Error("trop d'annonces"), ct).ConfigureAwait(false);
            return false;
        }

        if (RendezvousWire.TryReadAnnounce(frame, out var announcement, out var why) is false)
        {
            await session.SendAsync(RendezvousWire.Error(why!), ct).ConfigureAwait(false);
            return false;
        }

        var keys = announcement!.Tickets.Select(Convert.ToHexStringLower).Distinct(StringComparer.Ordinal).ToList();

        // Réannoncer les mêmes jetons ne compte pas : le plugin le fait à chaque
        // fenêtre. Seules les clés nouvelles peuvent faire dépasser le plafond.
        if (session.KeyCount + keys.Count(key => session.HasKey(key) is false) > Limits.MaxWaitingKeysPerSession)
        {
            await session.SendAsync(RendezvousWire.Error("trop d'annonces en attente sur cette connexion"), ct).ConfigureAwait(false);
            return false;
        }

        foreach (var key in keys)
        {
            // Le même client, arrivé par deux noms de ce service (un nom et son
            // IP, le service par défaut et son alias dans le cercle ouvert) :
            // même jeton et même bloc scellé, que son aléa rend unique à chaque
            // annonce. L'apparier avec lui-même lui renverrait ses propres
            // candidats et consumerait l'attente que son vrai pair cherche.
            if (_waiting.TryGetValue(key, out var self)
                && ReferenceEquals(self.Session, session) is false
                && self.SealedCandidates.AsSpan().SequenceEqual(announcement.SealedCandidates))
                continue;

            // Un pair déjà en attente sur ce jeton, et qui n'est pas nous : on
            // échange les blocs, sans jamais les lire.
            if (_waiting.TryGetValue(key, out var partner)
                && ReferenceEquals(partner.Session, session) is false
                && _waiting.TryRemove(new KeyValuePair<string, Waiting>(key, partner)))
            {
                partner.Session.ForgetKey(key);

                // Le partenaire est peut-être parti entre son annonce et la
                // nôtre : son échec ne doit pas couper notre session. On
                // reprend alors l'attente à sa place, comme s'il n'avait jamais
                // été là.
                if (await partner.Session.TrySendAsync(RendezvousWire.Matched(announcement.SealedCandidates), ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _matched);
                    await session.SendAsync(RendezvousWire.Matched(partner.SealedCandidates), ct).ConfigureAwait(false);

                    Note("appariement", $"[{key[..8]}] {partner.Session.Address} et {session.Address}");

                    // Un seul appariement par annonce : ses jetons désignent
                    // tous la même paire, et continuer ferait recevoir un
                    // second Matched pour le même pair.
                    return true;
                }
            }

            _waiting[key] = new Waiting
            {
                Session = session,
                SealedCandidates = announcement.SealedCandidates,
                Since = clock.UtcNow,
            };
            session.Remember(key);
        }

        return true;
    }

    /// <summary>
    /// Met bout à bout deux sessions qui n'ont pas réussi à se joindre en direct.
    /// </summary>
    /// <remarks>
    /// Le serveur ne voit que des octets déjà chiffrés de bout en bout par le
    /// canal des pairs. La promesse « aucun serveur ne détient un fichier »
    /// tient donc aussi ici : il transporte sans pouvoir lire.
    /// </remarks>
    private async Task<bool> HandleRelayAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (frame.Length != 1 + RendezvousTicket.SizeInBytes)
            return false;

        // Coupé depuis la console : refusé sans compter dans le limiteur et
        // sans couper la session, qui a peut-être des boîtes ouvertes. Le
        // plugin lit cette erreur comme un relais indisponible.
        if (Limits.RelayEnabled is false)
        {
            await session.SendAsync(RendezvousWire.Error("relais coupé sur ce service"), ct).ConfigureAwait(false);
            return true;
        }

        // Comptée comme une annonce : une demande de relais gare une socket
        // jusqu'à l'expiration, et c'est la trame la plus coûteuse à offrir.
        if (RateExceeded(session.Bucket, ref _refusedRelays))
        {
            await session.SendAsync(RendezvousWire.Error("trop de demandes de relais"), ct).ConfigureAwait(false);
            return false;
        }

        var key = Convert.ToHexStringLower(frame.AsSpan(1));

        // Une boucle, et on ne se gare que par TryAdd : les deux pairs passent
        // au relais après le même budget de perçage, donc souvent à la même
        // milliseconde. Avec un test puis une écriture, chacun ne voyait
        // personne, chacun se garait, et le second écrasait le premier. Les
        // deux attendaient alors un partenaire qui était déjà là.
        while (true)
        {
            if (_relayWaiting.TryGetValue(key, out var partner))
            {
                if (ReferenceEquals(partner.Session, session))
                    return true;

                // Un autre l'a pris entre-temps : on regarde à nouveau.
                if (_relayWaiting.TryRemove(new KeyValuePair<string, (PeerSession, DateTimeOffset)>(key, partner)) is false)
                    continue;

                partner.Session.ForgetKey(key);

                if (await partner.Session.TrySendAsync(RendezvousWire.Simple(RendezvousKind.RelayReady), ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _relayed);
                    Note("relais ouvert", $"[{key[..8]}] {partner.Session.Address} et {session.Address}");

                    await session.SendAsync(RendezvousWire.Simple(RendezvousKind.RelayReady), ct).ConfigureAwait(false);

                    Interlocked.Increment(ref _activeRelays);

                    try
                    {
                        await PeerSession.PipeAsync(partner.Session, session, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeRelays);
                    }

                    return true;
                }

                // Le partenaire garé est mort sans qu'on l'ait vu : on le libère
                // pour que sa boucle de service se termine, et on attend à sa place.
                partner.Session.ReleaseFromRelay();
                continue;
            }

            if (_relayWaiting.TryAdd(key, (session, clock.UtcNow)))
            {
                session.Remember(key);
                session.Park();
                return true;
            }
        }
    }

    private async Task<bool> HandleRegisterTicketAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        var payloadLength = frame.Length - 1 - RendezvousWire.InvitationTicketSize;

        if (payloadLength is <= 0 or > RendezvousWire.MaxTicketPayloadLength)
            return false;

        if (RateExceeded(session.Bucket, ref _refusedInvitations))
        {
            await session.SendAsync(RendezvousWire.Error("trop de dépôts"), ct).ConfigureAwait(false);
            return false;
        }

        var key = Convert.ToHexStringLower(frame.AsSpan(1, RendezvousWire.InvitationTicketSize));
        var invitation = new Invitation(
            frame.AsSpan(1 + RendezvousWire.InvitationTicketSize).ToArray(),
            clock.UtcNow + InvitationLifetime,
            session.Bucket);

        if (TryStoreInvitation(key, invitation) is false)
        {
            // Refusée mais sans couper : un foyer qui a beaucoup invité n'a
            // commis aucune faute de protocole, et le plugin sait lire ce refus.
            await session.SendAsync(RendezvousWire.Error("trop d'invitations en attente"), ct).ConfigureAwait(false);
            return true;
        }

        Note("invitation déposée", $"par {session.Address}");

        await session.SendAsync(RendezvousWire.Simple(RendezvousKind.TicketAccepted), ct).ConfigureAwait(false);
        return true;
    }

    private bool TryStoreInvitation(string key, Invitation invitation)
    {
        var limits = Limits;

        lock (_invitationGate)
        {
            // Redéposer le même ticket remplace l'ancien : il ne compte qu'une fois.
            var replaced = _invitations.GetValueOrDefault(key);
            var total = _invitations.Count - (replaced is null ? 0 : 1);
            var perBucket = _invitationsPerBucket.GetValueOrDefault(invitation.Bucket)
                            - (replaced?.Bucket == invitation.Bucket ? 1 : 0);

            if (total >= limits.MaxInvitations || perBucket >= limits.MaxInvitationsPerAddress)
                return false;

            if (replaced is not null)
                ReleaseInvitationLocked(replaced);

            _invitations[key] = invitation;
            _invitationsPerBucket[invitation.Bucket] = _invitationsPerBucket.GetValueOrDefault(invitation.Bucket) + 1;
            return true;
        }
    }

    private Invitation? TakeInvitation(string key)
    {
        lock (_invitationGate)
        {
            if (_invitations.Remove(key, out var invitation) is false)
                return null;

            ReleaseInvitationLocked(invitation);
            return invitation;
        }
    }

    private void ReleaseInvitationLocked(Invitation invitation)
    {
        var remaining = _invitationsPerBucket.GetValueOrDefault(invitation.Bucket) - 1;

        if (remaining <= 0)
            _invitationsPerBucket.Remove(invitation.Bucket);
        else
            _invitationsPerBucket[invitation.Bucket] = remaining;
    }

    private async Task<bool> HandleRedeemTicketAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (frame.Length != 1 + RendezvousWire.InvitationTicketSize)
            return false;

        // Le plafond d'essais est ce qui rend les quarante-huit bits du ticket
        // suffisants : sans lui, on pourrait les parcourir.
        if (RateExceeded(session.Bucket, ref _refusedInvitations))
        {
            await session.SendAsync(RendezvousWire.Error("trop d'essais"), ct).ConfigureAwait(false);
            return false;
        }

        var key = Convert.ToHexStringLower(frame.AsSpan(1, RendezvousWire.InvitationTicketSize));

        // Usage unique : retiré à la première lecture, y compris s'il a expiré.
        var invitation = TakeInvitation(key);

        if (invitation is null || invitation.Expiry < clock.UtcNow)
        {
            await session.SendAsync(RendezvousWire.Error("invitation inconnue, déjà utilisée ou expirée"), ct)
                         .ConfigureAwait(false);
            return true;
        }

        Note("invitation retirée", $"par {session.Address}");

        await session.SendAsync(RendezvousWire.TicketPayload(invitation.Payload), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Ouvre les boîtes d'un client, qui recevra les dépôts sur cette connexion.</summary>
    private async Task<bool> HandleMailboxOpenAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (RendezvousWire.TryReadAddresses(frame, out var addresses, out _) is false)
            return false;

        if (RateExceeded(session.Bucket, ref _refusedMailboxes))
        {
            await session.SendAsync(RendezvousWire.Error("trop d'ouvertures"), ct).ConfigureAwait(false);
            return false;
        }

        var keys = addresses.Select(Convert.ToHexStringLower).Distinct(StringComparer.Ordinal).ToList();

        if (session.MailboxCount + keys.Count(key => session.HasMailbox(key) is false) > Limits.MaxMailboxesPerSession)
        {
            await session.SendAsync(RendezvousWire.Error("trop de boîtes sur cette connexion"), ct).ConfigureAwait(false);
            return false;
        }

        lock (_mailboxGate)
        {
            foreach (var key in keys)
            {
                if (_mailboxes.TryGetValue(key, out var holders) is false)
                    _mailboxes[key] = holders = new HashSet<PeerSession>(ReferenceEqualityComparer.Instance);

                holders.Add(session);
                session.RememberMailbox(key);
            }
        }

        Note($"boîtes ouvertes ({keys.Count})", $"par {session.Address}");
        return true;
    }

    private async Task<bool> HandleMailboxQueryAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (RendezvousWire.TryReadAddresses(frame, out var addresses, out var why) is false)
        {
            await session.SendAsync(RendezvousWire.Error(why!), ct).ConfigureAwait(false);
            return false;
        }

        if (RateExceeded(session.Bucket, ref _refusedMailboxes))
        {
            await session.SendAsync(RendezvousWire.Error("trop d'interrogations"), ct).ConfigureAwait(false);
            return false;
        }

        List<bool> present;

        lock (_mailboxGate)
        {
            present = addresses
                .Select(address => _mailboxes.ContainsKey(Convert.ToHexStringLower(address)))
                .ToList();
        }

        await session.SendAsync(RendezvousWire.MailboxPresence(present), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Pousse une demande au destinataire, sans la lire.
    /// </summary>
    /// <remarks>
    /// Poussée et non mise en attente : le plugin garde une connexion ouverte,
    /// ce qui évite de transformer le rendez-vous en service de sondage
    /// interrogé par tous les clients toutes les secondes.
    ///
    /// Une demande à une boîte fermée est perdue, et c'est voulu : la garder
    /// ferait du serveur un dépôt de messages, donc un objet de rétention.
    /// </remarks>
    private async Task<bool> HandleMailboxDepositAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        var payloadLength = frame.Length - 1 - RendezvousWire.MailboxAddressSize;

        if (payloadLength is <= 0 or > RendezvousWire.MaxDepositLength)
            return false;

        if (RateExceeded(session.Bucket, ref _refusedMailboxes))
        {
            await session.SendAsync(RendezvousWire.Error("trop de dépôts"), ct).ConfigureAwait(false);
            return false;
        }

        var key = Convert.ToHexStringLower(frame.AsSpan(1, RendezvousWire.MailboxAddressSize));

        PeerSession[] recipients;

        lock (_mailboxGate)
            recipients = _mailboxes.TryGetValue(key, out var holders) ? [.. holders] : [];

        if (recipients.Length is 0)
        {
            await session.SendAsync(RendezvousWire.Error("destinataire absent"), ct).ConfigureAwait(false);
            return true;
        }

        Note("demande remise", $"[boîte {key[..8]}] de {session.Address}");

        var delivery = RendezvousWire.MailboxDelivery(frame.AsSpan(1 + RendezvousWire.MailboxAddressSize));

        // À chacune des sessions qui tiennent la boîte, et l'échec de l'une
        // ne prive ni les autres ni celui qui dépose.
        foreach (var recipient in recipients)
            await recipient.TrySendAsync(delivery, ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Compte une trame pour cette adresse, et dit si elle dépasse le débit.
    /// </summary>
    /// <param name="refused">Le compteur du motif, incrémenté en plus du total.</param>
    private bool RateExceeded(string bucket, ref long refused)
    {
        var now = clock.UtcNow;
        var entry = _rate.AddOrUpdate(
            bucket,
            _ => (1, now),
            (_, existing) => now - existing.Window > RateWindow
                ? (1, now)
                : (existing.Count + 1, existing.Window));

        if (entry.Count <= Limits.AnnouncementsPerMinute)
            return false;

        Interlocked.Increment(ref _rateRefusals);
        Interlocked.Increment(ref refused);
        return true;
    }

    private void Forget(PeerSession session)
    {
        // Par paire clé et objet, jamais par clé seule : une autre session peut
        // attendre sur le même jeton depuis, et son attente ne nous appartient pas.
        foreach (var key in session.Keys)
        {
            if (_waiting.TryGetValue(key, out var waiting) && ReferenceEquals(waiting.Session, session))
                _waiting.TryRemove(new KeyValuePair<string, Waiting>(key, waiting));

            if (_relayWaiting.TryGetValue(key, out var relay) && ReferenceEquals(relay.Session, session))
                _relayWaiting.TryRemove(new KeyValuePair<string, (PeerSession, DateTimeOffset)>(key, relay));
        }

        // Une boîte n'existe que tant que sa connexion tient : une déconnexion
        // vaut déclaration d'absence, sans délai ni battement de cœur à gérer.
        // Seule la session partante est retirée ; la boîte reste ouverte tant
        // qu'une autre la tient.
        lock (_mailboxGate)
        {
            foreach (var key in session.Mailboxes)
            {
                if (_mailboxes.TryGetValue(key, out var holders) && holders.Remove(session) && holders.Count is 0)
                    _mailboxes.Remove(key);
            }
        }
    }

    /// <summary>
    /// Oublie ce qui ne peut plus servir.
    /// </summary>
    /// <remarks>
    /// Une fenêtre de jeton dure dix minutes : au-delà, une attente ne peut plus
    /// aboutir, et la garder ferait du serveur un index de ce qu'il ne doit pas
    /// retenir. Toutes les dix secondes et non toutes les minutes : le relais
    /// abandonné se compte en dizaines de secondes, et un balayage coûte une
    /// lecture de quelques dictionnaires.
    /// </remarks>
    private async Task ExpireAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            await Task.Delay(SweepInterval, ct).ConfigureAwait(false);

            try
            {
                Sweep();
            }
            catch (Exception e)
            {
                // Une faute ici ne doit pas arrêter le balayage pour toujours.
                Log.WriteLine($"Balayage en échec ({e.GetType().Name}) : {e.Message}");
            }
        }
    }

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    /// <summary>Un passage du balayage, à l'heure de l'horloge injectée.</summary>
    /// <remarks>
    /// Public pour qu'un test le déclenche après avoir avancé l'horloge, au
    /// lieu d'attendre la période pour de vrai.
    /// </remarks>
    public void Sweep()
    {
        var now = clock.UtcNow;
        var limits = Limits;

        // Le balayage est le seul battement régulier du service : c'est lui
        // qui alimente l'historique, plutôt qu'une boucle de plus.
        History.Record(now, Snapshot());

        foreach (var (key, waiting) in _waiting)
        {
            if (waiting.Since < now - RendezvousTicket.Window
                && _waiting.TryRemove(new KeyValuePair<string, Waiting>(key, waiting)))
                waiting.Session.ForgetKey(key);
        }

        foreach (var (key, relay) in _relayWaiting)
        {
            if (relay.Since < now - limits.RelayWaitTimeout
                && _relayWaiting.TryRemove(new KeyValuePair<string, (PeerSession, DateTimeOffset)>(key, relay)))
            {
                relay.Session.ForgetKey(key);
                _ = AbandonRelayAsync(relay.Session);
            }
        }

        lock (_invitationGate)
        {
            foreach (var (key, invitation) in _invitations.ToList())
            {
                if (invitation.Expiry < now)
                {
                    _invitations.Remove(key);
                    ReleaseInvitationLocked(invitation);
                }
            }
        }

        // Une fenêtre close ne pèse plus sur personne : l'entrée s'efface, sans
        // quoi le limiteur garderait une trace de chaque adresse jamais vue.
        foreach (var (bucket, entry) in _rate)
        {
            if (now - entry.Window > RateWindow)
                _rate.TryRemove(new KeyValuePair<string, (int, DateTimeOffset)>(bucket, entry));
        }
    }

    /// <summary>Prévient une session garée que son pair n'est pas venu, et la libère.</summary>
    private static async Task AbandonRelayAsync(PeerSession session)
    {
        await session.TrySendAsync(RendezvousWire.Error("relais sans partenaire"), CancellationToken.None).ConfigureAwait(false);
        session.ReleaseFromRelay();
    }

    private static IPEndPoint Normalize(IPEndPoint endpoint)
        => endpoint.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port)
            : endpoint;
}
