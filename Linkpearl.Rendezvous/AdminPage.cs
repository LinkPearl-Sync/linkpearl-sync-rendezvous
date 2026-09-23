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
          :root {
            --fond: #12141a; --carte: #1a1d25; --creux: #15171e; --bord: #272b36;
            --texte: #e9e7e2; --doux: #949aa7; --nacre: #d3bd92; --vert: #7fb08a; --alerte: #d98a7a;
            --ombre: 0 1px 2px rgba(0,0,0,.4), 0 8px 24px rgba(0,0,0,.22);
          }
          * { box-sizing: border-box; }
          html { -webkit-text-size-adjust: 100%; }
          body {
            margin: 0; background: var(--fond); color: var(--texte);
            font: 15px/1.55 ui-sans-serif, system-ui, "Segoe UI", sans-serif;
            font-variant-numeric: tabular-nums;
          }
          .barre {
            position: sticky; top: 0; z-index: 5; display: flex; flex-wrap: wrap; gap: 1rem;
            align-items: center; justify-content: space-between;
            padding: .9rem 1.5rem; background: rgba(18,20,26,.92); backdrop-filter: blur(8px);
            border-bottom: 1px solid var(--bord);
          }
          .marque { display: flex; align-items: center; gap: .6rem; font-weight: 600; letter-spacing: .01em; }
          .perle {
            width: 12px; height: 12px; border-radius: 50%;
            background: radial-gradient(circle at 32% 30%, #fff, var(--nacre) 55%, #8d7c56);
            box-shadow: 0 0 10px rgba(211,189,146,.45);
          }
          .marque span.doux { font-weight: 400; }
          .lien { display: flex; align-items: center; gap: .45rem; font-size: .85rem; color: var(--doux); }
          .voyant { width: 8px; height: 8px; border-radius: 50%; background: var(--vert); }
          .voyant.perdu { background: var(--alerte); }
          main { max-width: 62rem; margin: 0 auto; padding: 1.75rem 1.5rem 4rem; }
          h2 {
            display: flex; align-items: center; gap: .5rem;
            font-size: .75rem; text-transform: uppercase; letter-spacing: .12em;
            color: var(--nacre); margin: 2.25rem 0 .75rem; font-weight: 600;
          }
          h2:first-of-type { margin-top: .5rem; }
          h2 svg { width: 14px; height: 14px; opacity: .8; }
          h2 .compte {
            margin-left: auto; color: var(--doux); font-weight: 400;
            letter-spacing: normal; text-transform: none; font-size: .8rem;
          }
          .panneau { background: var(--carte); border: 1px solid var(--bord); border-radius: 10px; box-shadow: var(--ombre); }
          .panneau > * { padding: 1rem 1.15rem; }
          .panneau > * + * { border-top: 1px solid var(--bord); }
          .doux { color: var(--doux); font-size: .85rem; }
          .chiffres { display: grid; grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr)); gap: .8rem; }
          .carte {
            background: var(--carte); border: 1px solid var(--bord); border-radius: 10px;
            padding: .9rem 1rem; box-shadow: var(--ombre);
          }
          .carte b { display: block; font-size: 1.65rem; font-weight: 600; line-height: 1.2; }
          .carte b em {
            font-style: normal; font-size: .9rem; font-weight: 400; color: var(--doux);
            margin-left: .2rem; vertical-align: .22em;
          }
          .carte span { color: var(--doux); font-size: .8rem; }
          .refus .carte b { font-size: 1.2rem; }
          .refus .carte b.non-nul { color: var(--alerte); }
          .boucles { display: grid; grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr)); }
          .boucles > * + * { border-top: none; }
          .boucle { display: flex; align-items: center; gap: .6rem; padding: .7rem 1rem; font-size: .9rem; }
          .boucle .doux { margin-left: auto; text-align: right; }
          @media (min-width: 640px) { .boucles > * + * { border-left: 1px solid var(--bord); } }
          .courbe { margin-top: .8rem; }
          .courbe svg { width: 100%; height: 56px; display: block; }
          .courbe .attente { display: flex; align-items: center; justify-content: center; height: 56px; }
          .courbe .attente[hidden] { display: none; }
          table { width: 100%; border-collapse: collapse; table-layout: fixed; }
          table col.large { width: 45%; }
          table col.moyenne { width: 35%; }
          table col.actions { width: 20%; }
          tbody tr:hover td { background: rgba(211,189,146,.04); }
          td { overflow: hidden; text-overflow: ellipsis; }
          th { text-align: left; font-size: .72rem; text-transform: uppercase; letter-spacing: .08em; color: var(--doux); font-weight: 600; }
          td, th { padding: .55rem .6rem; border-bottom: 1px solid var(--bord); vertical-align: middle; }
          tr:last-child td { border-bottom: none; }
          td.actions { text-align: right; white-space: nowrap; }
          code { font-family: ui-monospace, SFMono-Regular, monospace; font-size: .85em; color: var(--doux); }
          button {
            background: var(--creux); color: var(--texte); border: 1px solid var(--bord);
            border-radius: 6px; padding: .32rem .75rem; cursor: pointer; font: inherit; font-size: .85rem;
            transition: border-color .12s, color .12s;
          }
          button:hover { border-color: var(--nacre); color: var(--nacre); }
          button.danger:hover { border-color: var(--alerte); color: var(--alerte); }
          button.primaire { border-color: var(--nacre); color: var(--nacre); }
          button:focus-visible, input:focus-visible { outline: 2px solid var(--nacre); outline-offset: 1px; }
          input {
            background: var(--creux); color: var(--texte); border: 1px solid var(--bord);
            border-radius: 6px; padding: .4rem .65rem; font: inherit; font-size: .9rem; min-width: 0;
          }
          input::placeholder { color: #6f7583; }
          form { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; }
          form label { display: flex; flex-direction: column; gap: .25rem; font-size: .72rem; color: var(--doux); }
          .vide { color: var(--doux); font-size: .88rem; font-style: italic; }
          .mise-en-garde {
            display: flex; gap: .6rem; color: var(--doux); font-size: .85rem;
            border-left: 2px solid var(--nacre); padding-left: .8rem;
          }
          #toasts { position: fixed; right: 1.2rem; bottom: 1.2rem; display: flex; flex-direction: column; gap: .5rem; z-index: 10; }
          .toast {
            background: var(--carte); border: 1px solid var(--bord); border-left: 3px solid var(--vert);
            border-radius: 8px; padding: .6rem .9rem; font-size: .88rem; box-shadow: var(--ombre);
            animation: entree .18s ease-out;
          }
          .toast.rate { border-left-color: var(--alerte); }
          @keyframes entree { from { opacity: 0; transform: translateY(6px); } }
          @media (max-width: 640px) { main { padding: 1.25rem 1rem 3rem; } .barre { padding: .8rem 1rem; } }
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
          <div class="panneau courbe">
            <div>
              <div class="doux" style="display:flex;justify-content:space-between">
                <span>Boîtes ouvertes, trois dernières minutes</span><span id="creteBoites">0 au plus</span>
              </div>
              <svg id="sparkBoites" viewBox="0 0 300 56" preserveAspectRatio="none" aria-hidden="true" hidden></svg>
              <div class="attente doux" id="attenteCourbe">en attente d'une deuxième mesure</div>
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
                <label>Monde <input id="monde" placeholder="identifiant numérique" size="16" inputmode="numeric" required></label>
                <label>Motif <input id="motif" placeholder="contenu illegal" size="24" maxlength="120"></label>
                <button type="submit" class="primaire" style="align-self:flex-end">Ajouter</button>
              </form>
            </div>
          </div>
        </main>

        <div id="toasts"></div>

        <script>
        "use strict";
        const $ = (id) => document.getElementById(id);
        const histoire = [];
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
            body: corps ? JSON.stringify(corps) : undefined
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

        function courbe(svg, valeurs) {
          svg.replaceChildren();
          svg.hidden = valeurs.length < 2;
          $("attenteCourbe").hidden = valeurs.length >= 2;
          if (valeurs.length < 2) return;
          const haut = Math.max(1, ...valeurs);
          const pas = 300 / (valeurs.length - 1);
          const y = (v) => 52 - (v / haut) * 44;
          const points = valeurs.map((v, i) => (i * pas).toFixed(1) + "," + y(v).toFixed(1)).join(" ");

          const aire = document.createElementNS("http://www.w3.org/2000/svg", "polygon");
          aire.setAttribute("points", "0,56 " + points + " 300,56");
          aire.setAttribute("fill", "rgba(211,189,146,.12)");
          svg.appendChild(aire);

          const trait = document.createElementNS("http://www.w3.org/2000/svg", "polyline");
          trait.setAttribute("points", points);
          trait.setAttribute("fill", "none");
          trait.setAttribute("stroke", "#d3bd92");
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

          histoire.push(c.openMailboxes);
          if (histoire.length > 36) histoire.shift();
          courbe($("sparkBoites"), histoire);
          $("creteBoites").textContent = Math.max(0, ...histoire) + " au plus";

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

          remplir($("bannis"), s.bans.map(b => {
            const c = document.createElement("code");
            c.textContent = b.hash.slice(0, 16) + "...";
            c.title = b.hash;
            return ligne([c, b.reason, depuis(b.since),
              bouton("retirer", true, () => {
                if (confirm("Retirer cette entrée ? Le nom n'est pas conservé, donc elle ne se remet pas sans le ressaisir."))
                  return agir("/api/bans", "DELETE", { hash: b.hash }, "entrée retirée");
              })]);
          }), "liste vide. Personne n'est banni par ce service.");
        }

        async function bannir(e) {
          e.preventDefault();
          const nom = $("nom").value.trim();
          const monde = parseInt($("monde").value, 10);
          if (!confirm("Bannir " + nom + " sur le monde " + monde + " ?\nCette liste relève de la réputation, et le nom ne sera pas conservé.")) return;
          const ajoute = await agir("/api/bans", "POST",
            { name: nom, world: monde, reason: $("motif").value }, nom + " ajouté à la liste");
          if (ajoute) { $("nom").value = ""; $("motif").value = ""; }
        }

        // Une page laissée ouverte dans un onglet de fond n'a rien à interroger.
        document.addEventListener("visibilitychange", () => { if (!document.hidden) rafraichir(); });
        setInterval(() => { if (!document.hidden) rafraichir(); }, 5000);
        rafraichir();
        </script>
        </body>
        </html>
        """;
}
