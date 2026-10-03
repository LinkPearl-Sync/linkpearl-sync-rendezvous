using System.Globalization;
using System.Text;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Ce qu'un libellé de service venu du réseau a le droit de contenir.
/// </summary>
/// <remarks>
/// Le libellé est choisi par l'opérateur du service candidat, donc par
/// n'importe qui, et il finit dans pending.txt, dans authority.json, dans la
/// console et dans la liste signée que chaque client affiche. Un retour à la
/// ligne y écrivait une seconde candidature dans pending.txt, relue comme
/// telle ; un caractère de contrôle ou de mise en forme bidirectionnelle
/// (U+202E) faisait lire à l'opérateur et aux joueurs un autre nom que celui
/// qui est stocké. Le format de fil décode l'UTF-8 sans rien filtrer, et il
/// est une copie du plugin qu'on ne retouche pas ici : c'est donc au service
/// de refuser, avant tout stockage.
/// </remarks>
public static class ServiceLabel
{
    public static bool IsAcceptable(string label)
    {
        foreach (var rune in label.EnumerateRunes())
        {
            // Le caractère de remplacement trahit un UTF-8 invalide, que le
            // décodeur a réparé en silence : ce n'est pas ce que l'opérateur a écrit.
            if (rune == Rune.ReplacementChar)
                return false;

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse)
                return false;
        }

        return true;
    }
}
