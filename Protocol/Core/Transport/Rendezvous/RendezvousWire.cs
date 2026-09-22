using System.Buffers.Binary;

namespace Linkpearl.Core.Transport.Rendezvous;

/// <summary>Types de trame du rendez-vous.</summary>
public static class RendezvousKind
{
    public const byte Announce = 0x01;
    public const byte Matched = 0x02;
    public const byte RelayOpen = 0x03;
    public const byte RelayReady = 0x04;
    public const byte RelayData = 0x05;
    public const byte Error = 0x06;
    public const byte Reflect = 0x07;
    public const byte Reflected = 0x08;
    public const byte TicketRegister = 0x09;
    public const byte TicketAccepted = 0x0A;
    public const byte TicketRedeem = 0x0B;
    public const byte TicketPayload = 0x0C;

    /// <summary>Déclare sa présence et ouvre la remise des demandes.</summary>
    public const byte MailboxOpen = 0x0D;

    /// <summary>Demande lesquelles de ces adresses sont présentes.</summary>
    public const byte MailboxQuery = 0x0E;

    /// <summary>Réponse : un bit par adresse interrogée.</summary>
    public const byte MailboxPresence = 0x0F;

    /// <summary>Dépose une demande dans la boîte de quelqu'un.</summary>
    public const byte MailboxDeposit = 0x10;

    /// <summary>Remise poussée au destinataire.</summary>
    public const byte MailboxDelivery = 0x11;

    /// <summary>Un client demande à un service la liste de ceux qu'il connaît.</summary>
    public const byte DirectoryQuery = 0x12;

    /// <summary>La réponse, une liste d'adresses et de libellés.</summary>
    public const byte DirectoryList = 0x13;

    /// <summary>
    /// Un service se présente à un annuaire.
    /// </summary>
    /// <remarks>
    /// La seule trame qu'un service envoie à un autre, dans un seul sens, sans
    /// rien attendre en retour. Elle ne lui accorde aucune confiance : elle
    /// dépose une candidature dans une file que l'opérateur lira.
    /// </remarks>
    public const byte DirectorySubmit = 0x14;
}

/// <summary>Ce qu'un client annonce au rendez-vous.</summary>
/// <remarks>
/// Les jetons sont opaques, et le bloc de candidats est scellé sous une clé
/// dérivée du secret de paire : le serveur ne peut lire ni l'un ni l'autre. Il
/// ne voit que des octets et l'adresse d'où ils viennent.
/// </remarks>
public sealed record Announcement(IReadOnlyList<byte[]> Tickets, byte[] SealedCandidates);

/// <summary>Un service de rendez-vous tel qu'un annuaire le présente.</summary>
/// <remarks>
/// Le libellé vient du réseau : il est borné ici, et normalisé par l'interface
/// avant affichage, comme les noms de pairs.
/// </remarks>
public sealed record DirectoryEntry(string Address, string Label);

/// <summary>
/// Format de trame du rendez-vous, partagé par le client et le serveur.
/// </summary>
/// <remarks>
/// Partagé, et non dupliqué de part et d'autre : deux encodeurs qui divergent
/// donnent un échec silencieux qu'aucun des deux côtés ne peut diagnostiquer.
/// </remarks>
public static class RendezvousWire
{
    public const int MaxFrameLength = 64 * 1024;
    public const int MaxTicketsPerAnnouncement = 8;
    public const int MaxSealedCandidatesLength = 4096;

    public static byte[] Announce(Announcement announcement)
    {
        var body = new List<byte> { RendezvousKind.Announce, (byte)announcement.Tickets.Count };

        foreach (var ticket in announcement.Tickets)
            body.AddRange(ticket);

        body.Add((byte)(announcement.SealedCandidates.Length >> 8));
        body.Add((byte)announcement.SealedCandidates.Length);
        body.AddRange(announcement.SealedCandidates);

        return body.ToArray();
    }

    public static bool TryReadAnnounce(ReadOnlySpan<byte> frame, out Announcement? announcement, out string? rejection)
    {
        announcement = null;

        if (frame.Length < 2 || frame[0] != RendezvousKind.Announce)
        {
            rejection = "annonce malformée";
            return false;
        }

        var count = frame[1];

        if (count is 0 || count > MaxTicketsPerAnnouncement)
        {
            rejection = $"nombre de jetons hors bornes ({count}, plafond {MaxTicketsPerAnnouncement})";
            return false;
        }

        var offset = 2 + (count * RendezvousTicket.SizeInBytes);

        if (frame.Length < offset + 2)
        {
            rejection = "annonce tronquée";
            return false;
        }

        var tickets = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
            tickets.Add(frame.Slice(2 + (i * RendezvousTicket.SizeInBytes), RendezvousTicket.SizeInBytes).ToArray());

        var sealedLength = (frame[offset] << 8) | frame[offset + 1];
        offset += 2;

        if (sealedLength > MaxSealedCandidatesLength || frame.Length < offset + sealedLength)
        {
            rejection = "bloc de candidats hors bornes ou tronqué";
            return false;
        }

        announcement = new Announcement(tickets, frame.Slice(offset, sealedLength).ToArray());
        rejection = null;
        return true;
    }

    public static byte[] Matched(ReadOnlySpan<byte> sealedCandidates)
    {
        var frame = new byte[1 + sealedCandidates.Length];
        frame[0] = RendezvousKind.Matched;
        sealedCandidates.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] RelayOpen(ReadOnlySpan<byte> ticket)
    {
        var frame = new byte[1 + RendezvousTicket.SizeInBytes];
        frame[0] = RendezvousKind.RelayOpen;
        ticket[..RendezvousTicket.SizeInBytes].CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] Simple(byte kind) => [kind];

    public static byte[] RelayData(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + payload.Length];
        frame[0] = RendezvousKind.RelayData;
        payload.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] Error(string reason)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(reason);
        var frame = new byte[1 + text.Length];
        frame[0] = RendezvousKind.Error;
        text.CopyTo(frame.AsSpan(1));
        return frame;
    }

    /// <summary>
    /// Ce qu'un ticket d'invitation fait déposer au rendez-vous.
    /// </summary>
    /// <remarks>
    /// <b>Le serveur peut lire cette charge, et donc la remplacer.</b> C'est la
    /// contrepartie assumée d'un ticket de douze caractères : celui qui le
    /// retire n'a encore aucune clé pour ouvrir quoi que ce soit. La
    /// substitution se détecte après coup, en comparant les six mots que le
    /// handshake produit des deux côtés.
    /// </remarks>
    public const int MaxTicketPayloadLength = 256;

    public static byte[] TicketRegister(ReadOnlySpan<byte> ticket, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + InvitationTicketSize + payload.Length];
        frame[0] = RendezvousKind.TicketRegister;
        ticket[..InvitationTicketSize].CopyTo(frame.AsSpan(1));
        payload.CopyTo(frame.AsSpan(1 + InvitationTicketSize));
        return frame;
    }

    public static byte[] TicketRedeem(ReadOnlySpan<byte> ticket)
    {
        var frame = new byte[1 + InvitationTicketSize];
        frame[0] = RendezvousKind.TicketRedeem;
        ticket[..InvitationTicketSize].CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] TicketPayload(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + payload.Length];
        frame[0] = RendezvousKind.TicketPayload;
        payload.CopyTo(frame.AsSpan(1));
        return frame;
    }

    /// <summary>Taille d'un ticket sur le fil.</summary>
    public const int InvitationTicketSize = 6;

    /// <summary>Taille d'une adresse de boîte sur le fil.</summary>
    public const int MailboxAddressSize = 6;

    /// <summary>
    /// Adresses interrogeables en une fois.
    /// </summary>
    /// <remarks>
    /// Une zone bondée compte quelques dizaines de joueurs visibles. Le plafond
    /// évite qu'un client transforme le rendez-vous en service d'énumération.
    /// </remarks>
    public const int MaxQueriedAddresses = 64;

    public const int MaxDepositLength = 512;

    public static byte[] MailboxOpen(IReadOnlyList<byte[]> addresses)
    {
        var frame = new byte[2 + (addresses.Count * MailboxAddressSize)];
        frame[0] = RendezvousKind.MailboxOpen;
        frame[1] = (byte)addresses.Count;

        for (var i = 0; i < addresses.Count; i++)
            addresses[i].CopyTo(frame.AsSpan(2 + (i * MailboxAddressSize)));

        return frame;
    }

    public static byte[] MailboxQuery(IReadOnlyList<byte[]> addresses)
    {
        var frame = new byte[2 + (addresses.Count * MailboxAddressSize)];
        frame[0] = RendezvousKind.MailboxQuery;
        frame[1] = (byte)addresses.Count;

        for (var i = 0; i < addresses.Count; i++)
            addresses[i].CopyTo(frame.AsSpan(2 + (i * MailboxAddressSize)));

        return frame;
    }

    /// <summary>Lit une liste d'adresses, quel que soit le type de trame.</summary>
    public static bool TryReadAddresses(ReadOnlySpan<byte> frame, out List<byte[]> addresses, out string? rejection)
    {
        addresses = [];

        if (frame.Length < 2)
        {
            rejection = "trame trop courte";
            return false;
        }

        var count = frame[1];

        if (count is 0 || count > MaxQueriedAddresses)
        {
            rejection = $"nombre d'adresses hors bornes ({count}, plafond {MaxQueriedAddresses})";
            return false;
        }

        if (frame.Length < 2 + (count * MailboxAddressSize))
        {
            rejection = "trame tronquée";
            return false;
        }

        for (var i = 0; i < count; i++)
            addresses.Add(frame.Slice(2 + (i * MailboxAddressSize), MailboxAddressSize).ToArray());

        rejection = null;
        return true;
    }

    /// <summary>Un bit par adresse interrogée, dans l'ordre de la question.</summary>
    public static byte[] MailboxPresence(IReadOnlyList<bool> present)
    {
        var frame = new byte[2 + ((present.Count + 7) / 8)];
        frame[0] = RendezvousKind.MailboxPresence;
        frame[1] = (byte)present.Count;

        for (var i = 0; i < present.Count; i++)
        {
            if (present[i])
                frame[2 + (i / 8)] |= (byte)(1 << (i % 8));
        }

        return frame;
    }

    public static bool TryReadPresence(ReadOnlySpan<byte> frame, out bool[] present)
    {
        present = [];

        if (frame.Length < 2)
            return false;

        var count = frame[1];

        if (frame.Length < 2 + ((count + 7) / 8))
            return false;

        present = new bool[count];

        for (var i = 0; i < count; i++)
            present[i] = (frame[2 + (i / 8)] & (1 << (i % 8))) != 0;

        return true;
    }

    public static byte[] MailboxDeposit(ReadOnlySpan<byte> address, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + MailboxAddressSize + payload.Length];
        frame[0] = RendezvousKind.MailboxDeposit;
        address[..MailboxAddressSize].CopyTo(frame.AsSpan(1));
        payload.CopyTo(frame.AsSpan(1 + MailboxAddressSize));
        return frame;
    }

    public static byte[] MailboxDelivery(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + payload.Length];
        frame[0] = RendezvousKind.MailboxDelivery;
        payload.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public const int MaxDirectoryEntries = 64;
    public const int MaxDirectoryLabelLength = 64;
    public const int MaxDirectoryAddressLength = 255;

    /// <summary>
    /// La liste des services qu'un service connaît.
    /// </summary>
    /// <remarks>
    /// Elle vient de sa configuration, écrite par son opérateur, jamais d'un
    /// échange entre services : il publie ce qu'il connaît, il n'interroge
    /// personne.
    /// </remarks>
    public static byte[] Directory(IReadOnlyList<DirectoryEntry> entries)
        => WriteEntries(RendezvousKind.DirectoryList, entries);

    public static byte[] DirectorySubmit(string address, string label)
        => WriteEntries(RendezvousKind.DirectorySubmit, [new DirectoryEntry(address, label)]);

    private static byte[] WriteEntries(byte kind, IReadOnlyList<DirectoryEntry> entries)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(entries.Count, MaxDirectoryEntries);

        var bytes = new List<byte> { kind, (byte)entries.Count };

        foreach (var entry in entries)
        {
            var address = System.Text.Encoding.UTF8.GetBytes(entry.Address);
            var label = System.Text.Encoding.UTF8.GetBytes(entry.Label);

            if (address.Length is 0 or > MaxDirectoryAddressLength)
                throw new ArgumentException($"adresse hors bornes : {address.Length} octets", nameof(entries));

            if (label.Length > MaxDirectoryLabelLength)
                throw new ArgumentException($"libellé hors bornes : {label.Length} octets", nameof(entries));

            bytes.Add((byte)address.Length);
            bytes.AddRange(address);
            bytes.Add((byte)label.Length);
            bytes.AddRange(label);
        }

        return [.. bytes];
    }

    /// <summary>Lit une liste d'entrées, qu'elle vienne d'un annuaire ou d'une candidature.</summary>
    public static bool TryReadDirectory(
        ReadOnlySpan<byte> frame, out List<DirectoryEntry> entries, out string? rejection)
    {
        entries = [];

        if (frame.Length < 2)
        {
            rejection = "trame trop courte";
            return false;
        }

        var count = frame[1];

        if (count > MaxDirectoryEntries)
        {
            rejection = $"nombre d'entrées hors bornes ({count}, plafond {MaxDirectoryEntries})";
            return false;
        }

        var offset = 2;

        for (var i = 0; i < count; i++)
        {
            if (TryReadText(frame, ref offset, MaxDirectoryAddressLength, "adresse", out var address, out rejection) is false)
                return false;

            if (TryReadText(frame, ref offset, MaxDirectoryLabelLength, "libellé", out var label, out rejection) is false)
                return false;

            entries.Add(new DirectoryEntry(address, label));
        }

        rejection = null;
        return true;
    }

    private static bool TryReadText(
        ReadOnlySpan<byte> frame, ref int offset, int max, string what, out string value, out string? rejection)
    {
        value = string.Empty;

        if (offset >= frame.Length)
        {
            rejection = "trame tronquée";
            return false;
        }

        var length = frame[offset++];

        if (length > max)
        {
            rejection = $"{what} hors bornes ({length} octets, plafond {max})";
            return false;
        }

        if (offset + length > frame.Length)
        {
            rejection = "trame tronquée";
            return false;
        }

        value = System.Text.Encoding.UTF8.GetString(frame.Slice(offset, length));
        offset += length;
        rejection = null;
        return true;
    }

    /// <summary>Préfixe de longueur, pour délimiter les trames sur un flux.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> body)
    {
        var framed = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, body.Length);
        body.CopyTo(framed.AsSpan(sizeof(int)));
        return framed;
    }
}
