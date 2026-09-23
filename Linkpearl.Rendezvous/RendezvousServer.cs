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
/// Aucun état n'est persisté. Redémarrer le service n'efface rien puisqu'il n'y
/// a rien à effacer.
/// </remarks>
public sealed class RendezvousServer(int requestedPort, PeerDirectory directory, RendezvousLimits limits, IClock clock)
{
    private RendezvousLimits _limits = limits;

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
        int OpenMailboxes, int PendingAnnouncements, int RelayWaiting, int PendingInvitations,
        long Matches, long RelayedBytes,
        int Connections, long RefusedConnections, long RateRefusals, int TrackedAddresses,
        int KnownPeers, int PendingSubmissions);

    public Counters Snapshot()
    {
        int invitations;

        lock (_invitationGate)
            invitations = _invitations.Count;

        return new(
            _mailboxes.Count, _waiting.Count, _relayWaiting.Count, invitations,
            Matched, PeerSession.TotalRelayedBytes,
            Volatile.Read(ref _connections), Interlocked.Read(ref _refusedConnections),
            Interlocked.Read(ref _rateRefusals), _rate.Count,
            directory.Known().Count, directory.Pending().Count);
    }

    private long _rateRefusals;

    private int _connections;
    private long _refusedConnections;

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
    /// Les boîtes ouvertes, et la session qui les tient.
    /// </summary>
    /// <remarks>
    /// Une adresse de boîte dérive du nom de personnage, donc ce registre dit de
    /// fait qui est en ligne. C'est le prix assumé de la découvrabilité : un
    /// inconnu ne peut pas reconnaître quelqu'un sans que le serveur le puisse
    /// aussi. Rien n'est persisté, et l'adresse tourne toutes les trente
    /// minutes, ce qui empêche de relier deux périodes.
    /// </remarks>
    private readonly ConcurrentDictionary<string, PeerSession> _mailboxes = new(StringComparer.Ordinal);

    public long Matched { get; private set; }
    public long Relayed { get; private set; }

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

        Console.WriteLine($"Rendez-vous en écoute sur le port {Port}, TCP et UDP, IPv4 et IPv6.");
        Console.WriteLine("Aucun état persisté, aucune base de données.");
        Console.WriteLine();

        _listening.TrySetResult(Port);

        try
        {
            _ = Task.Run(() => ReflectAsync(reflection, ct), ct);
            _ = Task.Run(() => ExpireAsync(ct), ct);

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
                    Console.WriteLine($"Acceptation en échec ({e.GetType().Name}), reprise dans 100 ms.");
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    continue;
                }

                Admit(client, ct);
            }
        }
        finally
        {
            // Rendre les ports tout de suite : un test qui enchaîne des
            // services, ou un redémarrage rapide, ne doit pas tomber sur un
            // port encore tenu par l'instance précédente.
            listener.Stop();
            reflection.Dispose();
        }
    }

    /// <summary>Renvoie à un client l'adresse d'où on le voit.</summary>
    private static async Task ReflectAsync(UdpClient reflection, CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            UdpReceiveResult received;

            try
            {
                received = await reflection.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested is false)
            {
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (received.Buffer.Length == 0 || received.Buffer[0] != RendezvousKind.Reflect)
                continue;

            var from = Normalize(received.RemoteEndPoint);
            var address = from.Address.GetAddressBytes();

            var reply = new byte[2 + address.Length + 2];
            reply[0] = RendezvousKind.Reflected;
            reply[1] = (byte)address.Length;
            address.CopyTo(reply.AsSpan(2));
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2 + address.Length), (ushort)from.Port);

            await reflection.SendAsync(reply, received.RemoteEndPoint, ct).ConfigureAwait(false);
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
            Console.WriteLine($"Session en échec ({e.GetType().Name}) : {e.Message}");
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
            directory.Submit(session.Bucket, submitted[0]);

        return true;
    }

    private async Task<bool> HandleAnnounceAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (RateExceeded(session.Bucket))
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
            // Un pair déjà en attente sur ce jeton, et qui n'est pas nous : on
            // échange les blocs, sans jamais les lire.
            if (_waiting.TryRemove(key, out var partner) && ReferenceEquals(partner.Session, session) is false)
            {
                Matched++;
                session.Remember(key);

                await partner.Session.SendAsync(RendezvousWire.Matched(announcement.SealedCandidates), ct).ConfigureAwait(false);
                await session.SendAsync(RendezvousWire.Matched(partner.SealedCandidates), ct).ConfigureAwait(false);

                Console.WriteLine($"[{key[..8]}] appariés : {partner.Session.Address} et {session.Address}");
                continue;
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

        // Comptée comme une annonce : une demande de relais gare une socket
        // jusqu'à l'expiration, et c'est la trame la plus coûteuse à offrir.
        if (RateExceeded(session.Bucket))
        {
            await session.SendAsync(RendezvousWire.Error("trop de demandes de relais"), ct).ConfigureAwait(false);
            return false;
        }

        var key = Convert.ToHexStringLower(frame.AsSpan(1));

        if (_relayWaiting.TryRemove(key, out var partner) && ReferenceEquals(partner.Session, session) is false)
        {
            Relayed++;
            Console.WriteLine($"[{key[..8]}] relais ouvert entre {partner.Session.Address} et {session.Address}");

            await partner.Session.SendAsync(RendezvousWire.Simple(RendezvousKind.RelayReady), ct).ConfigureAwait(false);
            await session.SendAsync(RendezvousWire.Simple(RendezvousKind.RelayReady), ct).ConfigureAwait(false);

            await PeerSession.PipeAsync(partner.Session, session, ct).ConfigureAwait(false);
            return true;
        }

        _relayWaiting[key] = (session, clock.UtcNow);
        session.Remember(key);
        session.Park();
        return true;
    }

    private async Task<bool> HandleRegisterTicketAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        var payloadLength = frame.Length - 1 - RendezvousWire.InvitationTicketSize;

        if (payloadLength is <= 0 or > RendezvousWire.MaxTicketPayloadLength)
            return false;

        if (RateExceeded(session.Bucket))
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

        Console.WriteLine($"[{key}] invitation déposée par {session.Address}");

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
        if (RateExceeded(session.Bucket))
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

        Console.WriteLine($"[{key}] invitation retirée par {session.Address}");

        await session.SendAsync(RendezvousWire.TicketPayload(invitation.Payload), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Ouvre les boîtes d'un client, qui recevra les dépôts sur cette connexion.</summary>
    private async Task<bool> HandleMailboxOpenAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (RendezvousWire.TryReadAddresses(frame, out var addresses, out _) is false)
            return false;

        if (RateExceeded(session.Bucket))
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

        foreach (var key in keys)
        {
            _mailboxes[key] = session;
            session.RememberMailbox(key);
        }

        Console.WriteLine($"[boîtes] {addresses.Count} ouverte(s) par {session.Address}");
        return true;
    }

    private async Task<bool> HandleMailboxQueryAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (RendezvousWire.TryReadAddresses(frame, out var addresses, out var why) is false)
        {
            await session.SendAsync(RendezvousWire.Error(why!), ct).ConfigureAwait(false);
            return false;
        }

        if (RateExceeded(session.Bucket))
        {
            await session.SendAsync(RendezvousWire.Error("trop d'interrogations"), ct).ConfigureAwait(false);
            return false;
        }

        var present = addresses
            .Select(address => _mailboxes.ContainsKey(Convert.ToHexStringLower(address)))
            .ToList();

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

        if (RateExceeded(session.Bucket))
        {
            await session.SendAsync(RendezvousWire.Error("trop de dépôts"), ct).ConfigureAwait(false);
            return false;
        }

        var key = Convert.ToHexStringLower(frame.AsSpan(1, RendezvousWire.MailboxAddressSize));

        if (_mailboxes.TryGetValue(key, out var recipient) is false)
        {
            await session.SendAsync(RendezvousWire.Error("destinataire absent"), ct).ConfigureAwait(false);
            return true;
        }

        Console.WriteLine($"[boîte {key[..8]}] demande remise, de {session.Address}");

        await recipient.SendAsync(
            RendezvousWire.MailboxDelivery(frame.AsSpan(1 + RendezvousWire.MailboxAddressSize)), ct)
            .ConfigureAwait(false);

        return true;
    }

    private bool RateExceeded(string bucket)
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
        return true;
    }

    private void Forget(PeerSession session)
    {
        foreach (var key in session.Keys)
        {
            _waiting.TryRemove(new KeyValuePair<string, Waiting>(key, _waiting.GetValueOrDefault(key)!));

            if (_relayWaiting.TryGetValue(key, out var relay) && ReferenceEquals(relay.Session, session))
                _relayWaiting.TryRemove(new KeyValuePair<string, (PeerSession, DateTimeOffset)>(key, relay));
        }

        // Une boîte n'existe que tant que sa connexion tient : une déconnexion
        // vaut déclaration d'absence, sans délai ni battement de cœur à gérer.
        foreach (var key in session.Mailboxes)
            _mailboxes.TryRemove(new KeyValuePair<string, PeerSession>(key, session));
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
                Console.WriteLine($"Balayage en échec ({e.GetType().Name}) : {e.Message}");
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
