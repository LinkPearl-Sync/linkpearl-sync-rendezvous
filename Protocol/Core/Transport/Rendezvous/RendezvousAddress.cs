namespace Linkpearl.Core.Transport.Rendezvous;

/// <summary>
/// L'adresse d'un service de rendez-vous.
/// </summary>
/// <remarks>
/// Le port fait partie de l'adresse et non d'un réglage global, parce que la
/// fédération met plusieurs services en présence : la documentation recommande
/// déjà le 443 pour les réseaux restrictifs, donc deux d'entre eux peuvent
/// parfaitement écouter ailleurs.
///
/// La validation est stricte et sans traduction : un caractère inattendu fait
/// refuser plutôt que corriger. Cette chaîne arrive par la trame d'annuaire,
/// donc du réseau, et corriger reviendrait à valider une valeur puis à en
/// utiliser une autre.
/// </remarks>
public readonly record struct RendezvousAddress(string Host, int Port)
{
    public const int DefaultPort = 47900;

    private const int MaxHostLength = 255;

    public static bool TryParse(string? text, out RendezvousAddress address, out string? rejection)
    {
        address = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            rejection = "adresse vide";
            return false;
        }

        var cleaned = text.Trim();

        // Les caractères d'abord, le découpage ensuite. Dans l'ordre inverse,
        // « http://rdv.exemple.ch » se ferait refuser pour un port illisible,
        // alors que ce qu'il faut dire à qui colle une URL est que la barre
        // oblique n'a rien à faire ici.
        for (var i = 0; i < cleaned.Length; i++)
        {
            if (IsAllowed(cleaned[i]) is false && cleaned[i] is not ':')
            {
                // Le caractère fautif n'est pas repris dans la raison : il vient
                // du réseau et finirait dans un journal.
                rejection = $"caractère interdit en position {i}";
                return false;
            }
        }

        var separator = cleaned.LastIndexOf(':');

        var host = separator < 0 ? cleaned : cleaned[..separator];
        var port = DefaultPort;

        if (separator >= 0)
        {
            var written = cleaned[(separator + 1)..];

            if (int.TryParse(written, out port) is false || port is < 1 or > 65535)
            {
                rejection = "port hors bornes ou illisible";
                return false;
            }
        }

        if (host.Length is 0)
        {
            rejection = "adresse vide";
            return false;
        }

        if (host.Length > MaxHostLength)
        {
            rejection = $"hôte trop long ({host.Length} caractères, maximum {MaxHostLength})";
            return false;
        }

        address = new RendezvousAddress(host, port);
        rejection = null;
        return true;
    }

    /// <summary>Ce qu'un nom d'hôte ou une adresse littérale peut contenir.</summary>
    private static bool IsAllowed(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';

    public override string ToString() => Port == DefaultPort ? Host : $"{Host}:{Port}";
}
