namespace Linkpearl.Rendezvous;

/// <summary>
/// La page de la console, embarquée dans le binaire.
/// </summary>
/// <remarks>
/// Pas de cadre applicatif, pas de dépendance, pas de chaîne de construction :
/// une console d'administration qui en exigerait une serait une seconde chose à
/// maintenir, et elle tomberait en panne avant le service qu'elle surveille.
///
/// Elle ne demande pas de jeton : le navigateur l'a déjà donné pour obtenir
/// cette page, et le renvoie tout seul à chaque appel. Un champ de saisie ici
/// supposerait la page servie à qui la demande, ce qu'elle n'est plus.
/// </remarks>
public static class AdminPage
{
    public const string Html = """
        <!doctype html>
        <html lang="fr">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex, nofollow">
        <title>Linkpearl, service de rendez-vous</title>
        <style>
          /* Les couleurs, les polices et les formes du site public : la nuit du
             logo. Les polices ne sont pas chargées chez Google comme sur le
             site : une feuille tierce lirait le DOM de la console. Elles servent
             si la machine les a, sinon celles du système prennent le relais. */
          :root {
            color-scheme: dark;
            --deep: #07123a; --field: #040b26;
            --carte: rgba(255,255,255,.05); --creux: var(--field); --bord: rgba(143,196,255,.22); --trait: rgba(143,196,255,.14);
            --texte: #eaf1ff; --doux: #a9b8e6; --nacre: #5fb4ff; --pom: #ffa45c; --vert: #7fd6b0; --alerte: #ff8f7e;
            --ombre: 0 0 0 1px rgba(143,196,255,.06), 0 10px 30px rgba(2,6,24,.45);
            --display: "Fredoka", "Nunito", ui-rounded, system-ui, sans-serif;
            --body: "Nunito", ui-rounded, system-ui, sans-serif;
            --mono: "JetBrains Mono", ui-monospace, Consolas, monospace;
          }
          * { box-sizing: border-box; }
          html { -webkit-text-size-adjust: 100%; background: var(--deep); }
          body {
            margin: 0; min-height: 100vh; color: var(--texte);
            font: 15px/1.55 var(--body); font-variant-numeric: tabular-nums;
            background:
              radial-gradient(ellipse 70% 420px at 50% 120px, #1d3b9a 0%, transparent 70%),
              radial-gradient(ellipse at 50% 100%, #0c1e5c 0%, var(--deep) 60%);
            background-attachment: fixed;
          }
          .barre {
            position: sticky; top: 0; z-index: 5; display: flex; flex-wrap: wrap; gap: 1rem;
            align-items: center; justify-content: space-between;
            padding: .8rem 1.5rem; background: rgba(4,11,38,.78); backdrop-filter: blur(10px);
            border-bottom: 1px solid var(--bord);
          }
          .marque { display: flex; align-items: center; gap: .6rem; font-family: var(--display); font-weight: 600; font-size: 1.1rem; }
          .perle {
            width: 14px; height: 14px; border-radius: 50%;
            background: radial-gradient(circle at 35% 30%, #fff 0 18%, #bfe4ff 30%, #7a9cff 60%, #c79bff 100%);
            box-shadow: 0 0 12px #7ec3ff;
          }
          .marque span.doux { font-family: var(--body); font-weight: 400; font-size: .9rem; }
          .lien { display: flex; align-items: center; gap: .45rem; font-size: .85rem; color: var(--doux); }
          .voyant { width: 9px; height: 9px; border-radius: 50%; background: var(--vert); box-shadow: 0 0 8px var(--vert); }
          .voyant.perdu { background: var(--alerte); box-shadow: 0 0 8px var(--alerte); }
          main { max-width: 64rem; margin: 0 auto; padding: 1.75rem 1.5rem 4rem; }
          h2 {
            display: flex; align-items: center; gap: .55rem;
            font-family: var(--display); font-size: 1.2rem; font-weight: 600;
            color: var(--texte); margin: 2.25rem 0 .8rem;
          }
          main > h2:first-child { margin-top: .5rem; }
          h2 svg { width: 18px; height: 18px; color: var(--nacre); filter: drop-shadow(0 0 6px rgba(95,180,255,.5)); }
          h2 .compte { margin-left: auto; color: var(--doux); font-family: var(--body); font-weight: 400; font-size: .85rem; }
          .panneau { background: var(--carte); border: 1px solid var(--bord); border-radius: 18px; box-shadow: var(--ombre); overflow: hidden; }
          .panneau > * { padding: 1rem 1.2rem; }
          .panneau > * + * { border-top: 1px solid var(--trait); }
          .doux { color: var(--doux); font-size: .88rem; }
          .chiffres { display: grid; grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr)); gap: .8rem; }
          .carte {
            background: var(--carte); border: 1px solid var(--bord); border-radius: 18px;
            padding: .95rem 1.1rem; box-shadow: var(--ombre);
          }
          .carte b { display: block; font-family: var(--display); font-size: 1.75rem; font-weight: 600; line-height: 1.2; }
          .carte b em {
            font-style: normal; font-family: var(--body); font-size: .9rem; font-weight: 400; color: var(--doux);
            margin-left: .2rem; vertical-align: .22em;
          }
          .carte span { color: var(--doux); font-size: .82rem; }
          .refus .carte b { font-size: 1.25rem; }
          .refus .carte b.non-nul { color: var(--alerte); }
          .boucles { display: grid; grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr)); }
          .boucles > * + * { border-top: none; }
          .boucle { display: flex; align-items: center; gap: .6rem; padding: .75rem 1.1rem; font-size: .92rem; }
          .boucle .doux { margin-left: auto; text-align: right; }
          @media (min-width: 640px) { .boucles > * + * { border-left: 1px solid var(--trait); } }
          .courbe { margin-top: .8rem; }
          .fenetres {
            display: flex; gap: .3rem; align-items: center; margin: 0; border: none;
            padding: .6rem 1.2rem;
          }
          .fenetres legend { float: left; margin-right: .6rem; padding: 0; font-size: .82rem; color: var(--doux); }
          .fenetres label { position: relative; cursor: pointer; font-size: .85rem; font-weight: 700; }
          .fenetres label input { position: absolute; opacity: 0; width: 100%; height: 100%; margin: 0; cursor: pointer; }
          .fenetres label { display: inline-block; padding: .25rem .8rem; border: 1px solid var(--bord); border-radius: 999px; color: var(--doux); }
          .fenetres label:has(input:checked) { border-color: var(--nacre); color: var(--texte); background: rgba(95,180,255,.18); }
          .fenetres label:has(input:focus-visible) { outline: 2px solid var(--texte); outline-offset: 2px; }
          .courbe svg { width: 100%; height: 56px; display: block; }
          .courbe .attente { display: flex; align-items: center; justify-content: center; height: 56px; }
          .courbe .attente[hidden] { display: none; }
          table { width: 100%; border-collapse: collapse; table-layout: fixed; }
          table col.large { width: 45%; }
          table col.moyenne { width: 35%; }
          table col.actions { width: 20%; }
          thead th { background: rgba(4,11,38,.5); }
          tbody tr:hover td { background: rgba(95,180,255,.06); }
          td { overflow: hidden; text-overflow: ellipsis; }
          th { text-align: left; font-family: var(--display); font-size: .88rem; color: var(--doux); font-weight: 600; }
          td, th { padding: .6rem .7rem; border-bottom: 1px solid var(--trait); vertical-align: middle; }
          tr:last-child td { border-bottom: none; }
          td.actions { text-align: right; white-space: nowrap; }
          code { font-family: var(--mono); font-size: .84em; color: #cfe6ff; }
          button {
            background: rgba(95,180,255,.08); color: var(--texte); border: 1px solid var(--bord);
            border-radius: 999px; padding: .34rem .9rem; cursor: pointer; font: inherit; font-size: .85rem; font-weight: 700;
            transition: border-color .12s, color .12s, background .12s;
          }
          button:hover { border-color: var(--nacre); background: rgba(95,180,255,.16); }
          button.danger:hover { border-color: var(--alerte); color: var(--alerte); background: rgba(255,143,126,.1); }
          button.primaire { background: var(--pom); color: #2a1300; border-color: var(--pom); font-weight: 800; }
          button.primaire:hover { filter: brightness(1.08); background: var(--pom); }
          button:disabled { opacity: .55; cursor: default; }
          button:focus-visible, input:focus-visible { outline: 2px solid var(--texte); outline-offset: 2px; }
          input {
            background: var(--field); color: var(--texte); border: 1px solid var(--bord);
            border-radius: 12px; padding: .42rem .75rem; font: inherit; font-size: .9rem; min-width: 0;
          }
          input::placeholder { color: #6f7fb0; }
          select {
            background: var(--field); color: var(--texte); border: 1px solid var(--bord);
            border-radius: 12px; padding: .42rem .75rem; font: inherit; font-size: .9rem; min-width: 0; max-width: 14rem;
          }
          select:focus-visible { outline: 2px solid var(--texte); outline-offset: 2px; }
          .bouton {
            display: inline-block; background: rgba(95,180,255,.08); color: var(--texte); border: 1px solid var(--bord);
            border-radius: 999px; padding: .34rem .9rem; cursor: pointer; font-size: .85rem; font-weight: 700; text-decoration: none;
            transition: border-color .12s, background .12s;
          }
          .bouton:hover { border-color: var(--nacre); background: rgba(95,180,255,.16); }
          .bouton:focus-visible, .bouton:has(input:focus-visible) { outline: 2px solid var(--texte); outline-offset: 2px; }
          .outils { display: flex; flex-wrap: wrap; gap: .6rem; align-items: center; justify-content: space-between; }
          .outils .filtre { display: flex; align-items: center; gap: .5rem; font-size: .82rem; color: var(--doux); }
          .actions-liste { display: flex; gap: .5rem; flex-wrap: wrap; }
          form { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; }
          form label { display: flex; flex-direction: column; gap: .25rem; font-size: .76rem; color: var(--doux); font-weight: 700; }
          .grille-reglages { display: grid; grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr)); gap: .8rem; align-items: end; }
          .grille-reglages input[type=number] { width: 100%; }
          .grille-reglages .interrupteur { flex-direction: row; align-items: center; gap: .5rem; font-size: .9rem; color: var(--texte); padding-bottom: .45rem; }
          .grille-reglages .interrupteur input { accent-color: var(--nacre); width: 1rem; height: 1rem; margin: 0; }
          input:invalid { border-color: var(--alerte); }
          .vide { color: var(--doux); font-size: .9rem; font-style: italic; }
          .mise-en-garde {
            display: flex; gap: .6rem; color: var(--doux); font-size: .88rem;
            border-left: 3px solid var(--pom); padding-left: .85rem;
          }
          #toasts { position: fixed; right: 1.2rem; bottom: 1.2rem; display: flex; flex-direction: column; gap: .5rem; z-index: 10; }
          .toast {
            background: rgba(7,18,58,.95); border: 1px solid var(--bord); border-left: 3px solid var(--vert);
            border-radius: 14px; padding: .6rem .95rem; font-size: .88rem; box-shadow: var(--ombre);
            animation: entree .18s ease-out;
          }
          .toast.rate { border-left-color: var(--alerte); }
          @keyframes entree { from { opacity: 0; transform: translateY(6px); } }
          @media (prefers-reduced-motion: reduce) { .toast { animation: none; } }
          @media (max-width: 640px) { main { padding: 1.25rem 1rem 3rem; } .barre { padding: .75rem 1rem; } }
        </style>
        </head>
        <body>
        <header class="barre">
          <div class="marque"><span class="perle"></span> Linkpearl <span class="doux">service de rendez-vous</span></div>
          <div class="lien"><span class="voyant" id="voyant"></span><span id="entete">connexion...</span></div>
        </header>

        <main>
          <h2>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3v3M12 18v3M3 12h3M18 12h3"/><circle cx="12" cy="12" r="5"/></svg>
            État <span class="compte" id="sante"></span>
          </h2>
          <div class="chiffres">
            <div class="carte"><b id="connexions">0</b><span>connexions tenues</span></div>
            <div class="carte"><b id="relaisActifs">0</b><span>relais en cours</span></div>
            <div class="carte"><b id="invitations">0</b><span>invitations en attente</span></div>
            <div class="carte"><b id="memoire">0</b><span>ensemble de travail, <span id="tas">0</span> de tas géré</span></div>
          </div>
          <div class="panneau boucles" style="margin-top:.8rem">
            <div class="boucle"><span class="voyant" id="voyantAccept"></span><span>Acceptation TCP</span><span class="doux" id="etatAccept"></span></div>
            <div class="boucle"><span class="voyant" id="voyantReflect"></span><span>Réflexion UDP</span><span class="doux" id="etatReflect"></span></div>
          </div>

          <h2>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 12h4l3 8 4-16 3 8h4"/></svg>
            Trafic
          </h2>
          <div class="chiffres">
            <div class="carte"><b id="boites">0</b><span>boîtes ouvertes</span></div>
            <div class="carte"><b id="attente">0</b><span>annonces en attente</span></div>
            <div class="carte"><b id="appariements">0</b><span>appariements</span></div>
            <div class="carte"><b id="relaye">0</b><span>relayés depuis le démarrage</span></div>
          </div>
          <div class="chiffres refus" style="margin-top:.8rem">
            <div class="carte"><b id="refusAnnonce">0</b><span>annonces refusées</span></div>
            <div class="carte"><b id="refusBoite">0</b><span>boîtes refusées</span></div>
            <div class="carte"><b id="refusRelais">0</b><span>relais refusés</span></div>
            <div class="carte"><b id="refusInvitation">0</b><span>invitations refusées</span></div>
            <div class="carte"><b id="refusConnexion">0</b><span>connexions refusées</span></div>
          </div>
          <h2>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>
            Historique <span class="compte" id="fenetreTexte">trois dernières minutes, une mesure toutes les cinq secondes</span>
          </h2>
          <div class="panneau courbe" style="margin-top:0">
            <fieldset class="fenetres" id="fenetres">
              <legend>Fenêtre</legend>
              <label><input type="radio" name="fenetre" value="3m" checked>3 min</label>
              <label><input type="radio" name="fenetre" value="1h">1 h</label>
              <label><input type="radio" name="fenetre" value="24h">24 h</label>
            </fieldset>
            <div>
              <div class="doux" style="display:flex;justify-content:space-between">
                <span>Boîtes ouvertes</span><span id="creteBoites">0 au plus</span>
              </div>
              <svg id="sparkBoites" viewBox="0 0 300 56" preserveAspectRatio="none" aria-hidden="true" hidden></svg>
              <div class="attente doux" id="attenteBoites">en attente d'une deuxième mesure</div>
            </div>
            <div>
              <div class="doux" style="display:flex;justify-content:space-between">
                <span>Appariements</span><span id="creteAppariements">0 au plus</span>
              </div>
              <svg id="sparkAppariements" viewBox="0 0 300 56" preserveAspectRatio="none" aria-hidden="true" hidden></svg>
              <div class="attente doux" id="attenteAppariements">en attente d'une deuxième mesure</div>
            </div>
            <div>
              <div class="doux" style="display:flex;justify-content:space-between">
                <span>Octets relayés</span><span id="creteOctets">0 o au plus</span>
              </div>
              <svg id="sparkOctets" viewBox="0 0 300 56" preserveAspectRatio="none" aria-hidden="true" hidden></svg>
              <div class="attente doux" id="attenteOctets">en attente d'une deuxième mesure</div>
            </div>
            <div>
              <div class="doux" style="display:flex;justify-content:space-between">
                <span>Refus, limiteur et plafonds confondus</span><span id="creteRefus">0 au plus</span>
              </div>
              <svg id="sparkRefus" viewBox="0 0 300 56" preserveAspectRatio="none" aria-hidden="true" hidden></svg>
              <div class="attente doux" id="attenteRefus">en attente d'une deuxième mesure</div>
            </div>
          </div>

          <h2>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c5 5 5 13 0 18-5-5-5-13 0-18"/></svg>
            Annuaire <span class="compte" id="compteAnnuaire"></span>
          </h2>
          <div class="panneau">
            <div style="padding-bottom:.4rem">
              <table>
                <colgroup><col class="large"><col class="moyenne"><col class="actions"></colgroup>
                <thead><tr><th>Service connu</th><th>Libellé</th><th></th></tr></thead>
                <tbody id="connus"></tbody>
              </table>
            </div>
            <div>
              <p class="doux" style="margin:0 0 .5rem">Candidatures en attente de votre décision</p>
              <table>
                <colgroup><col class="large"><col class="moyenne"><col class="actions"></colgroup>
                <tbody id="candidats"></tbody>
              </table>
            </div>
          </div>

          <div id="autoriteSection" hidden>
            <h2>
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="4"/></svg>
              Cercle ouvert <span class="compte" id="compteAutorite"></span>
            </h2>
            <div class="panneau">
              <p class="doux" style="margin:0 0 .5rem; overflow-wrap:anywhere" id="autoriteEtat"></p>
              <div>
                <table>
                  <colgroup><col class="large"><col class="moyenne"><col class="moyenne"><col class="actions"></colgroup>
                  <thead><tr><th>Service</th><th>Libellé</th><th>État</th><th></th></tr></thead>
                  <tbody id="autorite"></tbody>
                </table>
              </div>
            </div>
          </div>

          <h2>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/><path d="M9 12l2 2 4-4"/></svg>
            Bannissements <span class="compte" id="compteBans"></span>
          </h2>
          <div class="panneau">
            <div class="mise-en-garde">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px;flex:0 0 16px;margin-top:.15rem"><path d="M12 9v5M12 17h.01"/><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/></svg>
              <span>
                Ce service ne voit aucun contenu, donc il ne peut vérifier aucune accusation :
                cette liste relève de la réputation, et celui qui l'applique en répond.
                Le nom saisi n'est pas conservé, seule son empreinte l'est, et le retirer de la
                liste ne permet pas de le relire.
              </span>
            </div>
            <div class="outils">
              <label class="filtre">Filtrer <input id="filtre" type="search" placeholder="motif ou empreinte" size="24" oninput="afficherBans()"></label>
              <span class="actions-liste">
                <a class="bouton" href="/api/bans/export" download="bans.json">Exporter bans.json</a>
                <label class="bouton">Importer un bans.json<input type="file" accept=".json,application/json" hidden onchange="importer(this)"></label>
              </span>
            </div>
            <div style="padding-bottom:.4rem">
              <table>
                <colgroup><col style="width:28%"><col style="width:34%"><col style="width:20%"><col style="width:18%"></colgroup>
                <thead><tr><th>Empreinte</th><th>Motif</th><th>Depuis</th><th></th></tr></thead>
                <tbody id="bannis"></tbody>
              </table>
            </div>
            <div>
              <form onsubmit="bannir(event)">
                <label>Personnage <input id="nom" placeholder="Nom Prénom" size="20" required></label>
                <label>Monde
                  <select id="monde" required onchange="$('mondeNumero').hidden = this.value !== 'autre'"></select>
                </label>
                <label id="mondeNumero" hidden>Identifiant
                  <input id="mondeId" type="number" min="1" max="65535" placeholder="numéro du monde" size="10">
                </label>
                <label>Motif <input id="motif" placeholder="contenu illegal" size="24" maxlength="120"></label>
                <span class="actions-liste" style="align-self:flex-end">
                  <button type="button" onclick="verifier()">Vérifier</button>
                  <button type="submit" class="primaire">Ajouter</button>
                </span>
              </form>
              <p class="doux" style="margin:.6rem 0 0">
                « Vérifier » dérive l'empreinte et dit si elle est listée, sans rien écrire.
                L'import fusionne par empreinte une liste exportée par un service qui partage le même sel.
              </p>
            </div>
          </div>
          <h2>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 6h10M18 6h2M4 12h2M10 12h10M4 18h12M20 18h0"/><circle cx="16" cy="6" r="2"/><circle cx="8" cy="12" r="2"/><circle cx="18" cy="18" r="2"/></svg>
            Réglages <span class="compte" id="etatReglages"></span>
          </h2>
          <div class="panneau">
            <div class="mise-en-garde">
              <span>
                Appliqués à chaud et écrits dans <code id="fichierReglages">settings.json</code>, qui surcharge la ligne de
                commande au prochain démarrage. Supprimer le fichier rend la main à la ligne de commande.
              </span>
            </div>
            <div>
              <form id="reglages" class="grille-reglages" onsubmit="enregistrerReglages(event)">
                <label>Annonces par minute et par adresse <input name="announcementsPerMinute" type="number" required></label>
                <label>Connexions, toutes adresses <input name="maxConnections" type="number" required></label>
                <label>Connexions par adresse <input name="maxConnectionsPerAddress" type="number" required></label>
                <label>Boîtes par connexion <input name="maxMailboxesPerSession" type="number" required></label>
                <label>Jetons en attente par connexion <input name="maxWaitingKeysPerSession" type="number" required></label>
                <label>Invitations en attente, au total <input name="maxInvitations" type="number" required></label>
                <label>Invitations en attente par adresse <input name="maxInvitationsPerAddress" type="number" required></label>
                <label class="interrupteur"><input name="relayEnabled" type="checkbox"> Relais actif</label>
                <span class="actions-liste" style="align-self:end">
                  <button type="button" onclick="chargerReglages()">Recharger</button>
                  <button type="submit" class="primaire">Enregistrer</button>
                </span>
              </form>
            </div>
          </div>
        </main>

        <div id="toasts"></div>

        <script>
        "use strict";
        const $ = (id) => document.getElementById(id);
        let dernier = null;

        function toast(texte, rate) {
          const t = document.createElement("div");
          t.className = rate ? "toast rate" : "toast";
          t.textContent = texte;
          $("toasts").appendChild(t);
          setTimeout(() => t.remove(), 4000);
        }

        async function appel(chemin, methode, corps) {
          // Le navigateur rejoue tout seul l'authentification qui a servi à
          // obtenir cette page : rien à saisir, rien à stocker ici.
          const r = await fetch(chemin, {
            method: methode,
            headers: { "Content-Type": "application/json" },
            // Un texte part tel quel : c'est un bans.json entier, déjà du JSON.
            body: corps === undefined ? undefined : (typeof corps === "string" ? corps : JSON.stringify(corps))
          });
          if (r.status === 401) { perdu("jeton refusé, rechargez la page"); return null; }
          return r;
        }

        function perdu(quoi) {
          $("voyant").classList.add("perdu");
          $("entete").textContent = quoi;
        }

        function duree(s) {
          const j = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
          if (j) return j + " j " + h + " h";
          if (h) return h + " h " + m + " min";
          return m + " min";
        }

        function octets(n) {
          const u = ["o", "Kio", "Mio", "Gio", "Tio"];
          let i = 0;
          while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
          return [n.toFixed(i ? 1 : 0), u[i]];
        }

        function grandeur(cible, n) {
          const [quantite, unite] = octets(n);
          cible.replaceChildren(document.createTextNode(quantite), Object.assign(document.createElement("em"), { textContent: unite }));
        }

        function ilYA(secondes) {
          const ecart = Math.max(0, Math.floor(Date.now() / 1000 - secondes));
          if (ecart < 60) return "à l'instant";
          if (ecart < 3600) return "il y a " + Math.floor(ecart / 60) + " min";
          return "il y a " + duree(ecart);
        }

        function boucle(nom, etat) {
          $("voyant" + nom).classList.toggle("perdu", !etat.alive);
          $("etat" + nom).textContent = (etat.alive ? "vivante" : "morte")
            + (etat.lastTurn ? ", dernier tour " + ilYA(etat.lastTurn) : ", jamais servi");
        }

        function depuis(secondes) {
          const jours = Math.floor((Date.now() / 1000 - secondes) / 86400);
          const date = new Date(secondes * 1000).toLocaleDateString("fr-CH");
          if (jours <= 0) return "aujourd'hui";
          if (jours === 1) return "hier";
          return date + " (" + jours + " j)";
        }

        // Trois minutes vues depuis la page, une mesure par rafraîchissement ;
        // au-delà, c'est le service qui tient l'anneau d'une journée.
        const series = { Boites: [], Appariements: [], Octets: [], Refus: [] };
        let fenetre = "3m";
        let precedent = null;
        let derniereHeure = 0;

        function dessiner(nom, valeurs, crete) {
          courbe($("spark" + nom), valeurs, $("attente" + nom));
          $("crete" + nom).textContent = crete(Math.max(0, ...valeurs)) + " au plus";
        }

        function dessinerTout(s) {
          dessiner("Boites", s.Boites, (n) => n);
          dessiner("Appariements", s.Appariements, (n) => n);
          dessiner("Octets", s.Octets, (n) => octets(n).join(" "));
          dessiner("Refus", s.Refus, (n) => n);
        }

        async function chargerHistorique() {
          const r = await appel("/api/history?range=" + fenetre, "GET");
          if (!r || !r.ok) return;
          const points = (await r.json()).points;
          derniereHeure = Date.now();
          dessinerTout({
            Boites: points.map((p) => p.openMailboxes),
            Appariements: points.map((p) => p.matches),
            Octets: points.map((p) => p.relayedBytes),
            Refus: points.map((p) => p.refusals)
          });
        }

        function changerFenetre(valeur) {
          fenetre = valeur;
          $("fenetreTexte").textContent = fenetre === "3m"
            ? "trois dernières minutes, une mesure toutes les cinq secondes"
            : (fenetre === "1h" ? "dernière heure" : "dernières vingt-quatre heures") + ", un point par minute";
          for (const nom in series) $("attente" + nom).textContent =
            fenetre === "3m" ? "en attente d'une deuxième mesure" : "chargement";
          if (fenetre === "3m") dessinerTout(series); else chargerHistorique();
        }

        $("fenetres").addEventListener("change", (e) => changerFenetre(e.target.value));

        function courbe(svg, valeurs, attente) {
          svg.replaceChildren();
          svg.hidden = valeurs.length < 2;
          attente.hidden = valeurs.length >= 2;
          if (valeurs.length < 2) return;
          const haut = Math.max(1, ...valeurs);
          const pas = 300 / (valeurs.length - 1);
          const y = (v) => 52 - (v / haut) * 44;
          const points = valeurs.map((v, i) => (i * pas).toFixed(1) + "," + y(v).toFixed(1)).join(" ");

          const aire = document.createElementNS("http://www.w3.org/2000/svg", "polygon");
          aire.setAttribute("points", "0,56 " + points + " 300,56");
          aire.setAttribute("fill", "rgba(95,180,255,.14)");
          svg.appendChild(aire);

          const trait = document.createElementNS("http://www.w3.org/2000/svg", "polyline");
          trait.setAttribute("points", points);
          trait.setAttribute("fill", "none");
          trait.setAttribute("stroke", "#5fb4ff");
          trait.setAttribute("stroke-width", "1.5");
          trait.setAttribute("vector-effect", "non-scaling-stroke");
          svg.appendChild(trait);
        }

        function ligne(cellules) {
          const tr = document.createElement("tr");
          cellules.forEach((c, i) => {
            const td = document.createElement("td");
            if (i === cellules.length - 1) td.className = "actions";
            if (c instanceof Node) td.appendChild(c); else td.textContent = c;
            tr.appendChild(td);
          });
          return tr;
        }

        function bouton(texte, danger, action) {
          const b = document.createElement("button");
          b.textContent = texte;
          if (danger) b.className = "danger";
          b.onclick = async () => { b.disabled = true; await action(); b.disabled = false; };
          return b;
        }

        function etatAutorite(p) {
          const noms = { Candidate: "candidat", Probation: "en probation", Listed: "listé", Delisted: "sorti", Vetoed: "écarté" };
          const ratio = p.probes ? " (" + Math.round(100 * p.successes / p.probes) + " %)" : "";
          return noms[p.standing] + ratio;
        }

        function remplir(corps, lignes, rien) {
          corps.replaceChildren();
          if (lignes.length === 0) {
            const tr = document.createElement("tr");
            const td = document.createElement("td");
            td.colSpan = 4; td.className = "vide"; td.textContent = rien;
            tr.appendChild(td); corps.appendChild(tr);
            return;
          }
          for (const l of lignes) corps.appendChild(l);
        }

        async function agir(chemin, methode, corps, fait) {
          const r = await appel(chemin, methode, corps);
          if (!r) return false;
          if (r.ok) { toast(fait); await rafraichir(); return true; }
          const erreur = await r.json().catch(() => ({}));
          toast(erreur.error || "refusé (" + r.status + ")", true);
          await rafraichir();
          return false;
        }

        async function rafraichir() {
          let s;
          try {
            const r = await appel("/api/status", "GET");
            if (!r || !r.ok) { perdu("état indisponible"); return; }
            s = await r.json();
          } catch (e) {
            perdu("service injoignable");
            return;
          }

          const c = s.counters, h = s.health, sain = h.healthy;
          $("voyant").classList.toggle("perdu", !sain);
          $("entete").textContent = "version " + s.version + ", port " + s.port + ", debout depuis " + duree(s.uptimeSeconds)
            + (sain ? "" : ", une boucle est morte");
          $("sante").textContent = sain ? "les deux boucles tournent" : "à redémarrer";
          boucle("Accept", h.accept);
          boucle("Reflect", h.reflect);

          $("connexions").textContent = c.connections;
          $("relaisActifs").textContent = c.activeRelays;
          $("invitations").textContent = c.pendingInvitations;
          grandeur($("memoire"), s.memory.workingSetBytes);
          $("tas").textContent = octets(s.memory.gcHeapBytes).join(" ");

          $("boites").textContent = c.openMailboxes;
          $("attente").textContent = c.pendingAnnouncements;
          $("appariements").textContent = c.matches;
          grandeur($("relaye"), c.relayedBytes);

          const r = s.refusals;
          for (const [id, n] of [["refusAnnonce", r.announce], ["refusBoite", r.mailbox], ["refusRelais", r.relay],
                                 ["refusInvitation", r.invitation], ["refusConnexion", r.connection]]) {
            $(id).textContent = n;
            $(id).classList.toggle("non-nul", n > 0);
          }

          $("compteAnnuaire").textContent =
            s.known.length + (s.known.length > 1 ? " services connus" : " service connu")
            + (s.pending.length ? ", " + s.pending.length + " en attente" : "");
          $("compteBans").textContent = s.bans.length + (s.bans.length > 1 ? " entrées" : " entrée");

          // Les compteurs sont cumulés depuis le démarrage ; la courbe montre
          // ce qui s'est passé entre deux mesures, comme le fait le service
          // pour ses minutes.
          const refus = c.rateRefusals + c.refusedConnections;
          series.Boites.push(c.openMailboxes);
          if (precedent) {
            series.Appariements.push(c.matches - precedent.matches);
            series.Octets.push(c.relayedBytes - precedent.relayedBytes);
            series.Refus.push(refus - precedent.refus);
          }
          precedent = { matches: c.matches, relayedBytes: c.relayedBytes, refus };
          for (const nom in series) if (series[nom].length > 36) series[nom].shift();

          if (fenetre === "3m") dessinerTout(series);
          else if (Date.now() - derniereHeure > 60000) chargerHistorique();

          remplir($("connus"), s.known.map(p => ligne([
            p.address, p.label || "",
            bouton("retirer", true, () => {
              if (confirm("Retirer " + p.address + " de l'annuaire ?"))
                return agir("/api/peers", "DELETE", { address: p.address }, p.address + " retiré");
            })
          ])), "aucun service connu. Une ligne dans peers.txt, ou une candidature approuvée ci-dessous.");

          remplir($("candidats"), s.pending.map(p => ligne([
            p.address, p.label || "",
            bouton("approuver", false, () => agir("/api/peers", "POST", { address: p.address }, p.address + " ajouté à l'annuaire"))
          ])), "aucune candidature. Un service qui s'annonce avec --announce-to apparaît ici.");

          const a = s.authority;
          $("autoriteSection").hidden = !a;
          if (a) {
            const listes = a.services.filter(p => p.standing === "Listed").length;
            $("compteAutorite").textContent = listes + (listes > 1 ? " services listés" : " service listé");
            $("autoriteEtat").textContent = "Clé publique " + a.publicKey + (a.version
              ? " · version " + a.version + ", valable jusqu'au " + new Date(a.expires * 1000).toLocaleString()
              : " · aucune liste émise");
            remplir($("autorite"), a.services.map(p => ligne([
              p.address, p.label || "", etatAutorite(p),
              p.standing === "Vetoed"
                ? bouton("rétablir", false, () => agir("/api/authority/veto", "DELETE", { address: p.address }, p.address + " rétabli"))
                : bouton("écarter", true, () => {
                    if (confirm("Écarter " + p.address + " du cercle ouvert ?"))
                      return agir("/api/authority/veto", "POST", { address: p.address }, p.address + " écarté");
                  })
            ])), "aucun service suivi. Une candidature reçue par --announce-to apparaît ici après la prochaine sonde.");
          }

          bansCourants = s.bans;
          afficherBans();
        }

        let bansCourants = [];

        function afficherBans() {
          const filtre = $("filtre").value.trim().toLowerCase();
          const visibles = bansCourants.filter(b => !filtre || b.reason.toLowerCase().includes(filtre) || b.hash.startsWith(filtre));
          remplir($("bannis"), visibles.map(b => {
            const c = document.createElement("code");
            c.textContent = b.hash.slice(0, 16) + "...";
            c.title = b.hash;
            return ligne([c, b.reason, depuis(b.since),
              bouton("retirer", true, () => {
                if (confirm("Retirer cette entrée ? Le nom n'est pas conservé, donc elle ne se remet pas sans le ressaisir."))
                  return agir("/api/bans", "DELETE", { hash: b.hash }, "entrée retirée");
              })]);
          }), bansCourants.length === 0
            ? "liste vide. Personne n'est banni par ce service."
            : "aucune entrée ne correspond au filtre.");
        }

        // La table des mondes vient du service, qui la tient à jour à la main :
        // la page n'en connaît aucun par elle-même.
        async function chargerMondes() {
          const r = await appel("/api/worlds", "GET");
          if (!r || !r.ok) return;
          const select = $("monde");
          const groupes = new Map();
          for (const m of (await r.json()).worlds) {
            const cle = m.dataCenter + " (" + m.region + ")";
            if (!groupes.has(cle)) groupes.set(cle, []);
            groupes.get(cle).push(m);
          }
          const invite = new Option("choisir un monde", "", true, true);
          invite.disabled = true;
          select.replaceChildren(invite);
          for (const [cle, mondes] of groupes) {
            const g = document.createElement("optgroup");
            g.label = cle;
            for (const m of mondes) g.appendChild(new Option(m.name, m.name));
            select.appendChild(g);
          }
          const autre = document.createElement("optgroup");
          autre.label = "Autre";
          autre.appendChild(new Option("par son identifiant numérique", "autre"));
          select.appendChild(autre);
        }

        function personnage() {
          const nom = $("nom").value.trim();
          const choix = $("monde").value;
          const monde = choix === "autre" ? $("mondeId").value.trim() : choix;
          if (!nom || !monde) { toast("nom et monde requis", true); return null; }
          return { name: nom, world: monde };
        }

        async function verifier() {
          const qui = personnage();
          if (!qui) return;
          const r = await appel("/api/bans/verify", "POST", qui);
          if (!r) return;
          const reponse = await r.json().catch(() => ({}));
          if (!r.ok) { toast(reponse.error || "refusé (" + r.status + ")", true); return; }
          toast(reponse.listed
            ? qui.name + " sur " + qui.world + " est dans la liste (" + reponse.hash.slice(0, 16) + "...)"
            : qui.name + " sur " + qui.world + " n'est pas dans la liste", !reponse.listed);
        }

        async function bannir(e) {
          e.preventDefault();
          const qui = personnage();
          if (!qui) return;
          if (!confirm("Bannir " + qui.name + " sur " + qui.world + " ?\nCette liste relève de la réputation, et le nom ne sera pas conservé.")) return;
          const ajoute = await agir("/api/bans", "POST",
            { ...qui, reason: $("motif").value }, qui.name + " ajouté à la liste");
          if (ajoute) { $("nom").value = ""; $("motif").value = ""; }
        }

        async function importer(champ) {
          const fichier = champ.files[0];
          champ.value = "";
          if (!fichier) return;
          const texte = await fichier.text();
          if (!confirm("Fusionner " + fichier.name + " dans la liste ? Les entrées déjà présentes gardent leur motif.")) return;
          const r = await appel("/api/bans/import", "POST", texte);
          if (!r) return;
          const reponse = await r.json().catch(() => ({}));
          if (!r.ok) { toast(reponse.error || "refusé (" + r.status + ")", true); return; }
          toast(reponse.added + (reponse.added > 1 ? " entrées ajoutées, " : " entrée ajoutée, ") + reponse.total + " au total");
          await rafraichir();
        }

        // Une page laissée ouverte dans un onglet de fond n'a rien à interroger.
        document.addEventListener("visibilitychange", () => { if (!document.hidden) rafraichir(); });
        setInterval(() => { if (!document.hidden) rafraichir(); }, 5000);
        // Les réglages se lisent une fois, et sur demande : ils ne bougent que
        // depuis cette page, et un rafraîchissement périodique écraserait ce
        // que l'opérateur est en train de taper.
        async function chargerReglages() {
          const r = await appel("/api/settings", "GET");
          if (!r || !r.ok) return;
          const s = await r.json();
          const form = $("reglages");
          for (const [nom, valeur] of Object.entries(s.values)) {
            const champ = form.elements[nom];
            if (!champ) continue;
            if (champ.type === "checkbox") champ.checked = valeur;
            else {
              champ.value = valeur;
              const borne = s.bounds[nom];
              if (borne) { champ.min = borne.min; champ.max = borne.max; champ.title = "entre " + borne.min + " et " + borne.max; }
            }
          }
          $("fichierReglages").textContent = s.file;
          $("etatReglages").textContent = s.persisted ? "surchargés par " + s.file : "valeurs de la ligne de commande";
        }

        async function enregistrerReglages(e) {
          e.preventDefault();
          const form = $("reglages");
          if (!form.reportValidity()) return;
          const corps = {};
          for (const champ of form.elements) {
            if (!champ.name) continue;
            corps[champ.name] = champ.type === "checkbox" ? champ.checked : parseInt(champ.value, 10);
          }
          const r = await appel("/api/settings", "PUT", corps);
          if (!r) return;
          const reponse = await r.json().catch(() => ({}));
          if (!r.ok) { toast(reponse.error || "refusé (" + r.status + ")", true); return; }
          toast("réglages appliqués et enregistrés");
          await chargerReglages();
        }

        chargerMondes();
        chargerReglages();
        rafraichir();
        </script>
        </body>
        </html>
        """;
}
