namespace Linkpearl.Rendezvous;

/// <summary>
/// La page de la console, embarquée dans le binaire.
/// </summary>
/// <remarks>
/// Pas de cadre applicatif, pas de dépendance, pas de chaîne de construction :
/// une console d'administration qui en exigerait une serait une seconde chose à
/// maintenir, et elle tomberait en panne avant le service qu'elle surveille.
/// </remarks>
public static class AdminPage
{
    public const string Html = """
        <!doctype html>
        <html lang="fr">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Linkpearl, service de rendez-vous</title>
        <style>
          :root {
            --fond: #14161c; --carte: #1c1f27; --bord: #2c313d;
            --texte: #e8e6e1; --doux: #9aa0ad; --nacre: #cbb58a; --alerte: #d98a7a;
          }
          * { box-sizing: border-box; }
          body {
            margin: 0; padding: 2rem 1rem; background: var(--fond); color: var(--texte);
            font: 15px/1.5 ui-sans-serif, system-ui, sans-serif;
          }
          main { max-width: 60rem; margin: 0 auto; }
          h1 { font-size: 1.3rem; font-weight: 600; margin: 0 0 .2rem; }
          h2 { font-size: .8rem; text-transform: uppercase; letter-spacing: .1em; color: var(--nacre); margin: 2rem 0 .6rem; }
          .doux { color: var(--doux); font-size: .85rem; }
          section { background: var(--carte); border: 1px solid var(--bord); border-radius: 8px; padding: 1rem; }
          .chiffres { display: grid; grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr)); gap: 1rem; }
          .chiffre b { display: block; font-size: 1.4rem; font-weight: 600; }
          table { width: 100%; border-collapse: collapse; }
          td, th { text-align: left; padding: .45rem .5rem; border-bottom: 1px solid var(--bord); }
          th { color: var(--doux); font-weight: 500; font-size: .8rem; }
          tr:last-child td { border-bottom: none; }
          code { font-family: ui-monospace, monospace; font-size: .85em; color: var(--doux); }
          button {
            background: transparent; color: var(--texte); border: 1px solid var(--bord);
            border-radius: 5px; padding: .3rem .7rem; cursor: pointer; font: inherit; font-size: .85rem;
          }
          button:hover { border-color: var(--nacre); color: var(--nacre); }
          button.danger:hover { border-color: var(--alerte); color: var(--alerte); }
          input {
            background: var(--fond); color: var(--texte); border: 1px solid var(--bord);
            border-radius: 5px; padding: .35rem .6rem; font: inherit; font-size: .9rem;
          }
          form { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; margin-top: .8rem; }
          .avertissement { color: var(--alerte); font-size: .85rem; margin-top: .8rem; }
          .vide { color: var(--doux); font-size: .9rem; padding: .3rem 0; }
        </style>
        </head>
        <body>
        <main>
          <h1>Service de rendez-vous Linkpearl</h1>
          <p class="doux" id="entete">Connexion...</p>

          <h2>Jeton</h2>
          <section>
            <p class="doux">Celui que le service a journalisé au premier demarrage, dans <code>admin.token</code>. Il reste dans ce navigateur.</p>
            <form onsubmit="poserJeton(event)">
              <input id="jeton" type="password" size="48" placeholder="jeton d'administration">
              <button type="submit">Retenir</button>
              <span class="doux" id="etatJeton"></span>
            </form>
          </section>

          <h2>Trafic</h2>
          <section class="chiffres">
            <div class="chiffre"><b id="boites">0</b><span class="doux">boites ouvertes</span></div>
            <div class="chiffre"><b id="attente">0</b><span class="doux">annonces en attente</span></div>
            <div class="chiffre"><b id="appariements">0</b><span class="doux">appariements</span></div>
            <div class="chiffre"><b id="relaye">0</b><span class="doux">relayes</span></div>
          </section>

          <h2>Annuaire</h2>
          <section>
            <table><thead><tr><th>Service connu</th><th>Libelle</th><th></th></tr></thead>
              <tbody id="connus"></tbody></table>
            <p class="doux" style="margin-top:1rem">Candidatures en attente</p>
            <table><tbody id="candidats"></tbody></table>
          </section>

          <h2>Bannissements</h2>
          <section>
            <p class="doux">
              Le service ne voit aucun contenu, donc il ne peut verifier aucune accusation :
              cette liste releve de la reputation, et celui qui l'applique en repond.
              Le nom n'est pas conserve, seule son empreinte l'est.
            </p>
            <table><thead><tr><th>Empreinte</th><th>Motif</th><th>Depuis</th><th></th></tr></thead>
              <tbody id="bannis"></tbody></table>
            <form onsubmit="bannir(event)">
              <input id="nom" placeholder="Nom du personnage" size="22">
              <input id="monde" placeholder="identifiant du monde" size="18" inputmode="numeric">
              <input id="motif" placeholder="motif" size="26">
              <button type="submit">Ajouter</button>
            </form>
            <p class="avertissement" id="avertissementBan"></p>
          </section>
        </main>

        <script>
        const $ = (id) => document.getElementById(id);
        let jeton = localStorage.getItem("lprdv-jeton") || "";
        $("jeton").value = jeton;

        function poserJeton(e) {
          e.preventDefault();
          jeton = $("jeton").value.trim();
          localStorage.setItem("lprdv-jeton", jeton);
          $("etatJeton").textContent = "retenu";
          rafraichir();
        }

        async function appel(chemin, methode, corps) {
          const r = await fetch(chemin, {
            method: methode,
            headers: jeton ? { "Authorization": "Bearer " + jeton, "Content-Type": "application/json" }
                           : { "Content-Type": "application/json" },
            body: corps ? JSON.stringify(corps) : undefined
          });
          if (r.status === 401) { $("etatJeton").textContent = "refuse"; return null; }
          return r;
        }

        function duree(s) {
          const j = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
          return (j ? j + " j " : "") + (h ? h + " h " : "") + m + " min";
        }

        function octets(n) {
          const u = ["o", "Kio", "Mio", "Gio", "Tio"];
          let i = 0;
          while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
          return n.toFixed(i ? 1 : 0) + " " + u[i];
        }

        function ligne(cellules) {
          const tr = document.createElement("tr");
          for (const c of cellules) {
            const td = document.createElement("td");
            if (c instanceof Node) td.appendChild(c); else td.textContent = c;
            tr.appendChild(td);
          }
          return tr;
        }

        function bouton(texte, danger, action) {
          const b = document.createElement("button");
          b.textContent = texte;
          if (danger) b.className = "danger";
          b.onclick = action;
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

        async function rafraichir() {
          const r = await appel("/api/status", "GET");
          if (!r || !r.ok) { $("entete").textContent = "jeton attendu"; return; }
          const s = await r.json();

          $("entete").textContent = "version " + s.version + ", port " + s.port + ", debout depuis " + duree(s.uptimeSeconds);
          $("etatJeton").textContent = "accepte";
          $("boites").textContent = s.openMailboxes;
          $("attente").textContent = s.pendingAnnouncements;
          $("appariements").textContent = s.matches;
          $("relaye").textContent = octets(s.relayedBytes);

          remplir($("connus"), s.known.map(p => ligne([
            p.address, p.label,
            bouton("retirer", true, async () => { await appel("/api/peers", "DELETE", { address: p.address }); rafraichir(); })
          ])), "aucun service connu");

          remplir($("candidats"), s.pending.map(p => ligne([
            p.address, p.label,
            bouton("approuver", false, async () => { await appel("/api/peers", "POST", { address: p.address }); rafraichir(); })
          ])), "aucune candidature");

          remplir($("bannis"), s.bans.map(b => {
            const c = document.createElement("code");
            c.textContent = b.hash.slice(0, 16) + "...";
            return ligne([c, b.reason, new Date(b.since * 1000).toLocaleDateString("fr-CH"),
              bouton("retirer", true, async () => { await appel("/api/bans", "DELETE", { hash: b.hash }); rafraichir(); })]);
          }), "liste vide");
        }

        async function bannir(e) {
          e.preventDefault();
          const monde = parseInt($("monde").value, 10);
          const r = await appel("/api/bans", "POST",
            { name: $("nom").value, world: monde, reason: $("motif").value });
          if (r && r.ok) {
            $("nom").value = ""; $("motif").value = "";
            $("avertissementBan").textContent = "";
          } else if (r) {
            $("avertissementBan").textContent = (await r.json()).error || "refuse";
          }
          rafraichir();
        }

        rafraichir();
        setInterval(rafraichir, 5000);
        </script>
        </body>
        </html>
        """;
}
