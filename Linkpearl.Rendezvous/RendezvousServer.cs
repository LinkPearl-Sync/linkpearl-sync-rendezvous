using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
public sealed class RendezvousServer(int port, int maxAnnouncementsPerMinute, PeerDirectory directory)
{
    /// <summary>
    /// Ce que le service a fait depuis son démarrage.
    /// </summary>
    /// <remarks>
    /// Tenu même sans personne pour le lire : une console d'administration
    /// viendra plus tard, et reconstituer ces compteurs après coup demanderait
    /// de retoucher chaque chemin de code. Aucune trame ne les expose.
    /// </remarks>
    public sealed record Counters(
        int OpenMailboxes, int PendingAnnouncements, long Matches, long RelayedBytes,
        int KnownPeers, int PendingSubmissions);

    public Counters Snapshot() => new(
        _mailboxes.Count, _waiting.Count, Matched, PeerSession.TotalRelayedBytes,
        directory.Known().Count, directory.Pending().Count);

    private sealed class Waiting
    {
        public required PeerSession Session { get; init; }
        public required byte[] SealedCandidates { get; init; }
        public required DateTime Since { get; init; }
    }

    private readonly ConcurrentDictionary<string, Waiting> _waiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PeerSession> _relayWaiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Count, DateTime Window)> _rate = new(StringComparer.Ordinal);

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
    private readonly ConcurrentDictionary<string, (byte[] Payload, DateTime Expiry)> _invitations =
        new(StringComparer.Ordinal);

    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(24);

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
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();

        var reflection = new UdpClient(AddressFamily.InterNetworkV6);
        reflection.Client.DualMode = true;
        reflection.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));

        Console.WriteLine($"Rendez-vous en écoute sur le port {port}, TCP et UDP, IPv4 et IPv6.");
        Console.WriteLine("Aucun état persisté, aucune base de données.");
        Console.WriteLine();

        _ = Task.Run(() => ReflectAsync(reflection, ct), ct);
        _ = Task.Run(() => ExpireAsync(ct), ct);

        while (ct.IsCancellationRequested is false)
        {
            var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            _ = Task.Run(() => ServeAsync(client, ct), ct);
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

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var session = new PeerSession(client);

        try
        {
            while (ct.IsCancellationRequested is false)
            {
                var frame = await session.ReadFrameAsync(ct).ConfigureAwait(false);

                if (frame is null)
                    return;

                var handled = frame[0] switch
                {
                    RendezvousKind.Announce => await HandleAnnounceAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.RelayOpen => await HandleRelayAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.TicketRegister => await HandleRegisterTicketAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.TicketRedeem => await HandleRedeemTicketAsync(session, frame, ct).ConfigureAwait(false),
                    RendezvousKind.MailboxOpen => HandleMailboxOpen(session, frame),
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
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            // Déconnexion ordinaire : rien à journaliser.
        }
        finally
        {
            Forget(session);
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
            directory.Submit(session.Address, submitted[0]);

        return true;
    }

    private async Task<bool> HandleAnnounceAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (RateExceeded(session.Address))
        {
            await session.SendAsync(RendezvousWire.Error("trop d'annonces"), ct).ConfigureAwait(false);
            return false;
        }

        if (RendezvousWire.TryReadAnnounce(frame, out var announcement, out var why) is false)
        {
            await session.SendAsync(RendezvousWire.Error(why!), ct).ConfigureAwait(false);
            return false;
        }

        foreach (var ticket in announcement!.Tickets)
        {
            var key = Convert.ToHexStringLower(ticket);

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
                Since = DateTime.UtcNow,
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

        var key = Convert.ToHexStringLower(frame.AsSpan(1));

        if (_relayWaiting.TryRemove(key, out var partner) && ReferenceEquals(partner, session) is false)
        {
            Relayed++;
            Console.WriteLine($"[{key[..8]}] relais ouvert entre {partner.Address} et {session.Address}");

            await partner.SendAsync(RendezvousWire.Simple(RendezvousKind.RelayReady), ct).ConfigureAwait(false);
            await session.SendAsync(RendezvousWire.Simple(RendezvousKind.RelayReady), ct).ConfigureAwait(false);

            await PeerSession.PipeAsync(partner, session, ct).ConfigureAwait(false);
            return true;
        }

        _relayWaiting[key] = session;
        session.Remember(key);
        session.Park();
        return true;
    }

    private async Task<bool> HandleRegisterTicketAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        var payloadLength = frame.Length - 1 - RendezvousWire.InvitationTicketSize;

        if (payloadLength is <= 0 or > RendezvousWire.MaxTicketPayloadLength)
            return false;

        if (RateExceeded(session.Address))
        {
            await session.SendAsync(RendezvousWire.Error("trop de dépôts"), ct).ConfigureAwait(false);
            return false;
        }

        var key = Convert.ToHexStringLower(frame.AsSpan(1, RendezvousWire.InvitationTicketSize));

        _invitations[key] = (
            frame.AsSpan(1 + RendezvousWire.InvitationTicketSize).ToArray(),
            DateTime.UtcNow + InvitationLifetime);

        Console.WriteLine($"[{key}] invitation déposée par {session.Address}");

        await session.SendAsync(RendezvousWire.Simple(RendezvousKind.TicketAccepted), ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleRedeemTicketAsync(PeerSession session, byte[] frame, CancellationToken ct)
    {
        if (frame.Length != 1 + RendezvousWire.InvitationTicketSize)
            return false;

        // Le plafond d'essais est ce qui rend les quarante-huit bits du ticket
        // suffisants : sans lui, on pourrait les parcourir.
        if (RateExceeded(session.Address))
        {
            await session.SendAsync(RendezvousWire.Error("trop d'essais"), ct).ConfigureAwait(false);
            return false;
        }

        var key = Convert.ToHexStringLower(frame.AsSpan(1, RendezvousWire.InvitationTicketSize));

        // Usage unique : retiré à la première lecture, y compris s'il a expiré.
        if (_invitations.TryRemove(key, out var invitation) is false || invitation.Expiry < DateTime.UtcNow)
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
    private bool HandleMailboxOpen(PeerSession session, byte[] frame)
    {
        if (RendezvousWire.TryReadAddresses(frame, out var addresses, out _) is false)
            return false;

        foreach (var address in addresses)
        {
            var key = Convert.ToHexStringLower(address);
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

        if (RateExceeded(session.Address))
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

        if (RateExceeded(session.Address))
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

    private bool RateExceeded(string address)
    {
        var now = DateTime.UtcNow;
        var entry = _rate.AddOrUpdate(
            address,
            _ => (1, now),
            (_, existing) => now - existing.Window > TimeSpan.FromMinutes(1)
                ? (1, now)
                : (existing.Count + 1, existing.Window));

        return entry.Count > maxAnnouncementsPerMinute;
    }

    private void Forget(PeerSession session)
    {
        foreach (var key in session.Keys)
        {
            _waiting.TryRemove(new KeyValuePair<string, Waiting>(key, _waiting.GetValueOrDefault(key)!));
            _relayWaiting.TryRemove(new KeyValuePair<string, PeerSession>(key, session));
        }

        // Une boîte n'existe que tant que sa connexion tient : une déconnexion
        // vaut déclaration d'absence, sans délai ni battement de cœur à gérer.
        foreach (var key in session.Mailboxes)
            _mailboxes.TryRemove(new KeyValuePair<string, PeerSession>(key, session));
    }

    /// <summary>
    /// Oublie les attentes trop anciennes.
    /// </summary>
    /// <remarks>
    /// Une fenêtre de jeton dure dix minutes : au-delà, une attente ne peut plus
    /// aboutir, et la garder ferait du serveur un index de ce qu'il ne doit pas
    /// retenir.
    /// </remarks>
    private async Task ExpireAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);

            var deadline = DateTime.UtcNow - RendezvousTicket.Window;

            foreach (var (key, waiting) in _waiting)
            {
                if (waiting.Since < deadline)
                    _waiting.TryRemove(key, out _);
            }

            foreach (var (key, invitation) in _invitations)
            {
                if (invitation.Expiry < DateTime.UtcNow)
                    _invitations.TryRemove(key, out _);
            }
        }
    }

    private static IPEndPoint Normalize(IPEndPoint endpoint)
        => endpoint.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port)
            : endpoint;
}
