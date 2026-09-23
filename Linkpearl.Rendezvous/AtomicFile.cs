namespace Linkpearl.Rendezvous;

/// <summary>
/// Remplace un fichier d'un bloc, ou pas du tout.
/// </summary>
/// <remarks>
/// Écrire en place laisse, le temps de l'écriture, un fichier tronqué que
/// tout lecteur prend pour le vrai : la liste de bannissement relue par un
/// client serait vide, l'annuaire muet. Une coupure au milieu le laisserait
/// ainsi pour de bon. Écrire à côté puis renommer est atomique sur les
/// systèmes de fichiers visés : l'ancien contenu reste intact jusqu'à ce que
/// le nouveau soit entier.
///
/// Sans forçage sur le disque : l'atomicité visée est celle d'un arrêt du
/// processus, pas d'une coupure de courant, et ces fichiers se réécrivent
/// depuis la console en cas de doute.
/// </remarks>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    public static void WriteAllLines(string path, IEnumerable<string> lines)
    {
        var temporary = path + ".tmp";

        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, overwrite: true);
    }
}
