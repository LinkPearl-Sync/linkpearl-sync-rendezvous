using System.Buffers.Binary;
using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;

namespace Linkpearl.Core.Transport.Rendezvous;

/// <summary>
/// Jeton tournant sous lequel une paire s'annonce au rendez-vous.
/// </summary>
/// <remarks>
/// Le serveur n'apprend ainsi ni clé publique, ni identité stable : seulement
/// que deux adresses partagent un jeton opaque pendant une fenêtre de dix
/// minutes. Un observateur ne peut pas relier deux fenêtres entre elles, donc
/// pas reconstituer un graphe de relations ni des horaires de présence.
///
/// Il reste les adresses IP, et cela est irréductible sans relais systématique.
/// Le modèle de menace le dit explicitement plutôt que de le passer sous
/// silence.
/// </remarks>
public sealed class RendezvousTicket(IClock clock)
{
    public const int SizeInBytes = 16;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static ReadOnlySpan<byte> Context => "linkpearl:rv:v1"u8;

    public byte[] Current(ReadOnlySpan<byte> pairSecret) => For(pairSecret, WindowIndex(clock.UtcNow));

    /// <summary>
    /// Les jetons à annoncer : la fenêtre courante et la suivante.
    /// </summary>
    /// <remarks>
    /// Sans la suivante, deux pairs de part et d'autre d'une bascule ne se
    /// verraient pas, et l'un des deux attendrait dix minutes sans comprendre
    /// pourquoi.
    /// </remarks>
    public IReadOnlyList<byte[]> Announce(ReadOnlySpan<byte> pairSecret)
    {
        var index = WindowIndex(clock.UtcNow);
        return [For(pairSecret, index), For(pairSecret, index + 1)];
    }

    private static long WindowIndex(DateTimeOffset now) => now.ToUnixTimeSeconds() / (long)Window.TotalSeconds;

    private static byte[] For(ReadOnlySpan<byte> pairSecret, long windowIndex)
    {
        Span<byte> message = stackalloc byte[Context.Length + sizeof(long)];
        Context.CopyTo(message);
        BinaryPrimitives.WriteInt64BigEndian(message[Context.Length..], windowIndex);

        Span<byte> full = stackalloc byte[32];
        HMACSHA256.HashData(pairSecret, message, full);

        return full[..SizeInBytes].ToArray();
    }
}
