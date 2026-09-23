namespace Linkpearl.Rendezvous;

/// <summary>Un monde de FFXIV : l'identifiant que porte l'empreinte, et le nom que tape l'opérateur.</summary>
public sealed record World(ushort Id, string Name, string DataCenter, string Region);

/// <summary>
/// Les mondes publics de FFXIV, pour choisir un monde par son nom.
/// </summary>
/// <remarks>
/// Une empreinte de bannissement porte l'identifiant numérique du monde,
/// que personne ne connaît par cœur ; un opérateur connaît « Ragnarok ».
/// Cette table fait la traduction, et elle se maintient à la main : à
/// l'ouverture d'un monde ou d'un centre de données, ajouter la ligne ici.
/// Un identifiant faux ferait une empreinte qui ne bannit personne, sans
/// qu'aucun test ne tombe, donc chaque ligne vient de la feuille
/// <c>World</c> du jeu (données Lumina), lue par XIVAPI v2 dans la version
/// de jeu <c>541c0c12e07da325</c> le 23 septembre 2026, filtrée sur
/// <c>IsPublic</c>. La saisie numérique reste possible pour un monde absent
/// d'ici. Les centres chinois et coréen ne figurent pas : ils ont leur propre
/// client et leur propre feuille.
/// </remarks>
public static class Worlds
{
    public static readonly IReadOnlyList<World> All =
    [
        // Chaos (Europe)
        new(80, "Cerberus", "Chaos", "Europe"),
        new(83, "Louisoix", "Chaos", "Europe"),
        new(71, "Moogle", "Chaos", "Europe"),
        new(39, "Omega", "Chaos", "Europe"),
        new(401, "Phantom", "Chaos", "Europe"),
        new(97, "Ragnarok", "Chaos", "Europe"),
        new(400, "Sagittarius", "Chaos", "Europe"),
        new(85, "Spriggan", "Chaos", "Europe"),
        // Light (Europe)
        new(402, "Alpha", "Light", "Europe"),
        new(36, "Lich", "Light", "Europe"),
        new(66, "Odin", "Light", "Europe"),
        new(56, "Phoenix", "Light", "Europe"),
        new(403, "Raiden", "Light", "Europe"),
        new(67, "Shiva", "Light", "Europe"),
        new(33, "Twintania", "Light", "Europe"),
        new(42, "Zodiark", "Light", "Europe"),
        // Shadow (Europe). Présents dans la feuille avec ces identifiants, mais
        // pas encore marqués publics à la date de l'extraction.
        new(418, "Eden", "Shadow", "Europe"),
        new(413, "Innocence", "Shadow", "Europe"),
        new(417, "Lakshmi", "Shadow", "Europe"),
        new(414, "Pixie", "Shadow", "Europe"),
        new(419, "Syldra", "Shadow", "Europe"),
        new(412, "Titania", "Shadow", "Europe"),
        new(415, "Tycoon", "Shadow", "Europe"),
        new(416, "Wyvern", "Shadow", "Europe"),
        // Aether (Amérique du Nord)
        new(73, "Adamantoise", "Aether", "Amérique du Nord"),
        new(79, "Cactuar", "Aether", "Amérique du Nord"),
        new(54, "Faerie", "Aether", "Amérique du Nord"),
        new(63, "Gilgamesh", "Aether", "Amérique du Nord"),
        new(40, "Jenova", "Aether", "Amérique du Nord"),
        new(65, "Midgardsormr", "Aether", "Amérique du Nord"),
        new(99, "Sargatanas", "Aether", "Amérique du Nord"),
        new(57, "Siren", "Aether", "Amérique du Nord"),
        // Crystal (Amérique du Nord)
        new(91, "Balmung", "Crystal", "Amérique du Nord"),
        new(34, "Brynhildr", "Crystal", "Amérique du Nord"),
        new(74, "Coeurl", "Crystal", "Amérique du Nord"),
        new(62, "Diabolos", "Crystal", "Amérique du Nord"),
        new(81, "Goblin", "Crystal", "Amérique du Nord"),
        new(75, "Malboro", "Crystal", "Amérique du Nord"),
        new(37, "Mateus", "Crystal", "Amérique du Nord"),
        new(41, "Zalera", "Crystal", "Amérique du Nord"),
        // Dynamis (Amérique du Nord)
        new(408, "Cuchulainn", "Dynamis", "Amérique du Nord"),
        new(411, "Golem", "Dynamis", "Amérique du Nord"),
        new(406, "Halicarnassus", "Dynamis", "Amérique du Nord"),
        new(409, "Kraken", "Dynamis", "Amérique du Nord"),
        new(407, "Maduin", "Dynamis", "Amérique du Nord"),
        new(404, "Marilith", "Dynamis", "Amérique du Nord"),
        new(410, "Rafflesia", "Dynamis", "Amérique du Nord"),
        new(405, "Seraph", "Dynamis", "Amérique du Nord"),
        // Primal (Amérique du Nord)
        new(78, "Behemoth", "Primal", "Amérique du Nord"),
        new(93, "Excalibur", "Primal", "Amérique du Nord"),
        new(53, "Exodus", "Primal", "Amérique du Nord"),
        new(35, "Famfrit", "Primal", "Amérique du Nord"),
        new(95, "Hyperion", "Primal", "Amérique du Nord"),
        new(55, "Lamia", "Primal", "Amérique du Nord"),
        new(64, "Leviathan", "Primal", "Amérique du Nord"),
        new(77, "Ultros", "Primal", "Amérique du Nord"),
        // Elemental (Japon)
        new(90, "Aegis", "Elemental", "Japon"),
        new(68, "Atomos", "Elemental", "Japon"),
        new(45, "Carbuncle", "Elemental", "Japon"),
        new(58, "Garuda", "Elemental", "Japon"),
        new(94, "Gungnir", "Elemental", "Japon"),
        new(49, "Kujata", "Elemental", "Japon"),
        new(72, "Tonberry", "Elemental", "Japon"),
        new(50, "Typhon", "Elemental", "Japon"),
        // Gaia (Japon)
        new(43, "Alexander", "Gaia", "Japon"),
        new(69, "Bahamut", "Gaia", "Japon"),
        new(92, "Durandal", "Gaia", "Japon"),
        new(46, "Fenrir", "Gaia", "Japon"),
        new(59, "Ifrit", "Gaia", "Japon"),
        new(98, "Ridill", "Gaia", "Japon"),
        new(76, "Tiamat", "Gaia", "Japon"),
        new(51, "Ultima", "Gaia", "Japon"),
        // Mana (Japon)
        new(44, "Anima", "Mana", "Japon"),
        new(23, "Asura", "Mana", "Japon"),
        new(70, "Chocobo", "Mana", "Japon"),
        new(47, "Hades", "Mana", "Japon"),
        new(48, "Ixion", "Mana", "Japon"),
        new(96, "Masamune", "Mana", "Japon"),
        new(28, "Pandaemonium", "Mana", "Japon"),
        new(61, "Titan", "Mana", "Japon"),
        // Meteor (Japon)
        new(24, "Belias", "Meteor", "Japon"),
        new(82, "Mandragora", "Meteor", "Japon"),
        new(60, "Ramuh", "Meteor", "Japon"),
        new(29, "Shinryu", "Meteor", "Japon"),
        new(30, "Unicorn", "Meteor", "Japon"),
        new(52, "Valefor", "Meteor", "Japon"),
        new(31, "Yojimbo", "Meteor", "Japon"),
        new(32, "Zeromus", "Meteor", "Japon"),
        // Materia (Océanie)
        new(22, "Bismarck", "Materia", "Océanie"),
        new(21, "Ravana", "Materia", "Océanie"),
        new(86, "Sephirot", "Materia", "Océanie"),
        new(87, "Sophia", "Materia", "Océanie"),
        new(88, "Zurvan", "Materia", "Océanie"),
    ];

    private static readonly Dictionary<string, World> ByName =
        All.ToDictionary(world => world.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Résout ce que l'opérateur a tapé : un nom de la table, ou un identifiant.
    /// </summary>
    /// <remarks>
    /// L'identifiant reste accepté tel quel, sans le chercher dans la table :
    /// un monde ouvert hier n'y est pas encore, et l'opérateur qui en
    /// connaît le numéro ne doit pas être bloqué par une liste en retard.
    /// </remarks>
    public static bool TryResolve(string? input, out ushort id)
    {
        id = 0;

        var text = input?.Trim() ?? "";

        if (text.Length is 0)
            return false;

        if (ByName.TryGetValue(text, out var world))
        {
            id = world.Id;
            return true;
        }

        return ushort.TryParse(text, out id) && id > 0;
    }

    /// <summary>Le nom d'un identifiant, ou null s'il n'est pas dans la table.</summary>
    public static string? NameOf(ushort id)
        => All.FirstOrDefault(world => world.Id == id)?.Name;
}
