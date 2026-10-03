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

    /// <summary>
    /// Connexions TCP tenues en même temps, toutes adresses confondues.
    /// </summary>
    /// <remarks>
    /// L'unité systemd donne LimitNOFILE=65536 : la moitié laisse au
    /// processus de quoi ouvrir ses fichiers, la console et ses sondes même
    /// plein. Un joueur tient d'ordinaire deux à cinq connexions et jusqu'à
    /// vingt et une en pointe, donc 32 768 places servent de 1 500 joueurs
    /// tous en pointe à plus de 6 000 en régime courant. L'ancien plafond,
    /// 2 048, s'occupait entier depuis 64 adresses.
    /// </remarks>
    public int MaxConnections { get; init; } = 32_768;

    /// <summary>Connexions TCP tenues en même temps depuis une même adresse (un /64 en IPv6).</summary>
    public int MaxConnectionsPerAddress { get; init; } = 32;

    /// <summary>
    /// Connexions TCP tenues en même temps depuis un même /48 IPv6.
    /// </summary>
    /// <remarks>
    /// Quatre seaux ordinaires pleins : de quoi servir une petite structure
    /// ou plusieurs foyers d'un même opérateur, sans qu'un /48 loué chez un
    /// hébergeur, qui porte 65 536 /64, ne prenne le service entier.
    /// </remarks>
    public int MaxConnectionsPerPrefix { get; init; } = 128;

    /// <summary>
    /// Délai au bout duquel une session qui ne tient rien est fermée.
    /// </summary>
    /// <remarks>
    /// Sans boîte, sans attente ni relais, une connexion n'attend plus que de
    /// poser sa prochaine question. Le plugin ne la garde pas plus de
    /// vingt-cinq secondes après son appariement : une minute ne coupe
    /// personne de légitime, et rend la place à qui en a besoin.
    /// </remarks>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

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

    /// <summary>
    /// Sessions qui peuvent tenir ensemble une même boîte non réclamée.
    /// </summary>
    /// <remarks>
    /// Plusieurs détenteurs ont une raison d'être : deux clients sur la même
    /// machine, une reconnexion dont l'ancienne session n'est pas encore
    /// tombée. Quatre couvrent ces cas avec de la marge. Au-delà, n'importe
    /// qui pouvait s'abonner par centaines à une boîte de présence et
    /// démultiplier chaque dépôt qu'elle reçoit.
    /// </remarks>
    public int MaxMailboxHolders { get; init; } = 4;

    /// <summary>Jetons sur lesquels une même connexion peut attendre un pair.</summary>
    public int MaxWaitingKeysPerSession { get; init; } = 64;

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
    /// Temps accordé à une session pour prendre une trame qu'on lui envoie.
    /// </summary>
    /// <remarks>
    /// Une trame de rendez-vous pèse au plus 64 Kio, et un client vivant la
    /// prend dans son tampon en quelques millisecondes : cinq secondes
    /// laissent passer un lien mobile engorgé, et coupent celui qui a cessé
    /// de lire avant qu'il ne retienne trop longtemps ceux qui lui écrivent.
    /// </remarks>
    public TimeSpan PeerSendTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Temps au bout duquel un relais dont un côté ne prend plus rien est coupé.
    /// </summary>
    /// <remarks>
    /// Trente secondes sans qu'un seul bloc de 16 Kio passe : aucun lien en
    /// état de servir une apparence ne reste aussi longtemps à l'arrêt, et
    /// le pair, de son côté, abandonne bien avant.
    /// </remarks>
    public TimeSpan RelayStallTimeout { get; init; } = TimeSpan.FromSeconds(30);

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
