using System.Text.Json;
using System.Text.Json.Nodes;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Les réglages que la console peut changer à chaud, et leur fichier.
/// </summary>
/// <remarks>
/// La ligne de commande donne les valeurs de départ ; <c>settings.json</c>,
/// s'il existe, les surcharge. C'est l'ordre qui permet de régler depuis la
/// page sans redémarrer, et de retrouver le réglage au redémarrage suivant,
/// tout en gardant une installation sans fichier identique à ce qu'elle
/// était. Seuls les plafonds y figurent : les délais et le keepalive sont
/// des constantes de protocole, pas des boutons d'exploitation.
///
/// Chaque valeur a une borne, et une valeur hors borne est refusée avant
/// d'être appliquée : un zéro dans le plafond de connexions couperait tout
/// le monde, et un fichier réécrit à la main avec une faute ne doit pas
/// rendre le service muet au démarrage. Un fichier illisible est ignoré,
/// et dit, plutôt que réécrit : l'opérateur doit pouvoir le corriger.
/// </remarks>
public sealed class SettingsStore(string path)
{
    /// <summary>Un réglage : son nom dans le fichier et la page, et ce qu'il accepte.</summary>
    public sealed record Bound(string Name, long Min, long Max);

    /// <summary>
    /// Les bornes, uniques pour la validation et pour les champs de la page.
    /// </summary>
    /// <remarks>
    /// Les plafonds bas sont à un : un plafond nul refuserait tout sans le
    /// dire. Les plafonds hauts sont larges mais finis, pour qu'une faute
    /// de frappe ne fasse pas d'un service un puits sans fond.
    /// </remarks>
    public static readonly IReadOnlyList<Bound> Bounds =
    [
        new("announcementsPerMinute", 1, 100_000),
        new("maxConnections", 1, 1_000_000),
        new("maxConnectionsPerAddress", 1, 100_000),
        new("maxMailboxesPerSession", 1, 1_024),
        new("maxWaitingKeysPerSession", 1, 4_096),
        new("maxInvitations", 1, 10_000_000),
        new("maxInvitationsPerAddress", 1, 100_000),
    ];

    public const string RelayEnabledName = "relayEnabled";

    public string Path => path;

    /// <summary>
    /// Les réglages de départ, surchargés par le fichier s'il existe et se lit.
    /// </summary>
    public RendezvousLimits Load(RendezvousLimits defaults, TextWriter log)
    {
        if (File.Exists(path) is false)
            return defaults;

        JsonObject? document;

        try
        {
            document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException e)
        {
            log.WriteLine($"Réglages illisibles dans {path} ({e.Message}), ligne de commande conservée.");
            return defaults;
        }

        if (document is null)
        {
            log.WriteLine($"Réglages refusés dans {path} (le document n'est pas un objet), ligne de commande conservée.");
            return defaults;
        }

        if (TryApply(defaults, document, out var loaded, out var why) is false)
        {
            log.WriteLine($"Réglages refusés dans {path} ({why}), ligne de commande conservée.");
            return defaults;
        }

        log.WriteLine($"Réglages relus depuis {path}.");
        return loaded;
    }

    /// <summary>Écrit les réglages d'un bloc.</summary>
    public void Save(RendezvousLimits limits)
        => AtomicFile.WriteAllText(path, ToJson(limits).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// Applique un document partiel ou complet, ou dit pourquoi il ne le peut pas.
    /// </summary>
    /// <remarks>
    /// Tout est validé avant que rien ne soit appliqué : un réglage sur deux
    /// changé serait pire qu'aucun. Un champ inconnu est une erreur et non
    /// un silence, parce que c'est presque toujours une faute de frappe sur
    /// un champ connu.
    /// </remarks>
    public static bool TryApply(RendezvousLimits current, JsonObject document, out RendezvousLimits applied, out string? why)
    {
        applied = current;

        foreach (var (name, node) in document)
        {
            if (name == RelayEnabledName)
            {
                if (node is not JsonValue flag || flag.TryGetValue<bool>(out _) is false)
                {
                    why = $"{name} doit valoir true ou false";
                    return false;
                }

                continue;
            }

            var bound = Bounds.FirstOrDefault(b => b.Name == name);

            if (bound is null)
            {
                why = $"réglage inconnu : {name}";
                return false;
            }

            if (node is not JsonValue value || value.TryGetValue<long>(out var number) is false)
            {
                why = $"{name} doit être un entier";
                return false;
            }

            if (number < bound.Min || number > bound.Max)
            {
                why = $"{name} doit être entre {bound.Min} et {bound.Max}";
                return false;
            }
        }

        foreach (var (name, node) in document)
        {
            applied = name switch
            {
                "announcementsPerMinute" => applied with { AnnouncementsPerMinute = (int)node!.GetValue<long>() },
                "maxConnections" => applied with { MaxConnections = (int)node!.GetValue<long>() },
                "maxConnectionsPerAddress" => applied with { MaxConnectionsPerAddress = (int)node!.GetValue<long>() },
                "maxMailboxesPerSession" => applied with { MaxMailboxesPerSession = (int)node!.GetValue<long>() },
                "maxWaitingKeysPerSession" => applied with { MaxWaitingKeysPerSession = (int)node!.GetValue<long>() },
                "maxInvitations" => applied with { MaxInvitations = (int)node!.GetValue<long>() },
                "maxInvitationsPerAddress" => applied with { MaxInvitationsPerAddress = (int)node!.GetValue<long>() },
                RelayEnabledName => applied with { RelayEnabled = node!.GetValue<bool>() },
                _ => applied,
            };
        }

        why = null;
        return true;
    }

    /// <summary>Les réglages tels que la page et le fichier les voient.</summary>
    public static JsonObject ToJson(RendezvousLimits limits)
        => new()
        {
            ["announcementsPerMinute"] = limits.AnnouncementsPerMinute,
            ["maxConnections"] = limits.MaxConnections,
            ["maxConnectionsPerAddress"] = limits.MaxConnectionsPerAddress,
            ["maxMailboxesPerSession"] = limits.MaxMailboxesPerSession,
            ["maxWaitingKeysPerSession"] = limits.MaxWaitingKeysPerSession,
            ["maxInvitations"] = limits.MaxInvitations,
            ["maxInvitationsPerAddress"] = limits.MaxInvitationsPerAddress,
            [RelayEnabledName] = limits.RelayEnabled,
        };

    /// <summary>Ce qui a changé entre deux jeux de réglages, pour le journal.</summary>
    public static IReadOnlyList<string> Changes(RendezvousLimits before, RendezvousLimits after)
    {
        var from = ToJson(before);
        var to = ToJson(after);
        var changes = new List<string>();

        foreach (var (name, node) in to)
        {
            if (from[name]?.ToJsonString() != node?.ToJsonString())
                changes.Add($"{name} {from[name]?.ToJsonString()} -> {node?.ToJsonString()}");
        }

        return changes;
    }
}
