namespace Linkpearl.Rendezvous;

/// <summary>
/// Les plafonds du service, réunis pour qu'une console puisse les régler.
/// </summary>
/// <remarks>
/// Un record immuable : on en fabrique un nouveau avec <c>with</c> et on le
/// substitue d'un bloc, plutôt que de laisser chaque chemin de code lire un
/// réglage qui change sous ses pieds à mi-parcours.
///
/// Les valeurs par défaut viennent de ce que fait le plugin. Il ouvre une
/// connexion par pair à joindre, tenue jusqu'à vingt-cinq secondes, et une
/// autre pour la présence : un joueur avec vingt pairs hors ligne en tient
/// donc vingt et une depuis une même adresse, et deux clients derrière la même
/// box en tiennent le double. Un plafond de huit par adresse, qui a été
/// envisagé, les aurait coupés. Il ouvre deux boîtes par connexion, la
/// fenêtre courante et la suivante ; seize laisse de la marge sans permettre
/// de tenir la moitié de l'annuaire depuis une seule socket.
/// </remarks>
public sealed record RendezvousLimits
{
    public static readonly RendezvousLimits Default = new();

    /// <summary>Trames comptées par minute et par adresse, au-delà desquelles on refuse.</summary>
    public int AnnouncementsPerMinute { get; init; } = 60;

    /// <summary>Connexions TCP tenues en même temps, toutes adresses confondues.</summary>
    public int MaxConnections { get; init; } = 2048;

    /// <summary>Connexions TCP tenues en même temps depuis une même adresse (un /64 en IPv6).</summary>
    public int MaxConnectionsPerAddress { get; init; } = 32;

    /// <summary>
    /// Délai accordé à une connexion pour dire sa première trame.
    /// </summary>
    /// <remarks>
    /// Une connexion qui n'a rien à dire n'est qu'un descripteur occupé : un
    /// client qui se connecte et se tait par milliers épuiserait la table sans
    /// jamais envoyer un octet. Le plugin parle dès la connexion établie, donc
    /// dix secondes ne coupent personne de légitime.
    /// </remarks>
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Boîtes qu'une même connexion peut tenir ouvertes.
    /// </summary>
    /// <remarks>
    /// Deux personnelles, et deux de présence plus deux d'admission par groupe
    /// au changement de fenêtre : 42 pour les dix groupes qu'un personnage peut
    /// avoir. Le client se reconnecte avant d'atteindre la limite.
    /// </remarks>
    public int MaxMailboxesPerSession { get; init; } = 64;

    /// <summary>Jetons sur lesquels une même connexion peut attendre un pair.</summary>
    public int MaxWaitingKeysPerSession { get; init; } = 64;

    /// <summary>Invitations déposées et non encore retirées, toutes adresses confondues.</summary>
    public int MaxInvitations { get; init; } = 10_000;

    /// <summary>Invitations déposées et non encore retirées depuis une même adresse.</summary>
    public int MaxInvitationsPerAddress { get; init; } = 32;

    /// <summary>
    /// Si le service accepte de relayer.
    /// </summary>
    /// <remarks>
    /// Le relais est ce qui coûte de la bande passante à l'opérateur, et la
    /// seule chose qu'il peut vouloir couper sans arrêter le reste : un
    /// service qui ne relaie plus apparie encore, et les pairs qui se
    /// joignent en direct ne voient rien. Coupé, une demande de relais
    /// reçoit une erreur et la connexion continue.
    /// </remarks>
    public bool RelayEnabled { get; init; } = true;

    /// <summary>
    /// Temps pendant lequel une demande de relais attend son pair.
    /// </summary>
    /// <remarks>
    /// Les deux pairs demandent le relais après avoir échoué en direct, donc à
    /// quelques secondes l'un de l'autre. Au-delà, l'autre ne viendra pas, et
    /// garder la session garée reviendrait à laisser n'importe qui immobiliser
    /// une socket pour toujours.
    /// </remarks>
    public TimeSpan RelayWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Keepalive TCP sur chaque socket acceptée.
    /// </summary>
    /// <remarks>
    /// Une boîte n'existe que tant que sa connexion tient, donc une connexion
    /// morte sans FIN (câble tiré, veille, NAT qui oublie) laisserait une boîte
    /// ouverte sur un joueur parti, et un descripteur occupé, jusqu'à ce que
    /// le noyau abandonne, ce qui se compte en heures. Soixante secondes de
    /// silence, puis trois sondes à dix secondes : au plus une minute et demie
    /// pour constater la mort, sans trafic notable pour un client vivant.
    /// </remarks>
    public TimeSpan KeepAliveTime { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);

    public int KeepAliveRetryCount { get; init; } = 3;
}
