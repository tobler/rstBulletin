// Kleine Helfer – kein Framework nötig.
(function () {
  // Bild-Lightbox für Anhänge
  document.addEventListener('click', function (e) {
    var a = e.target.closest && e.target.closest('a[data-lightbox]');
    if (a) {
      e.preventDefault();
      var box = document.createElement('div');
      box.className = 'lightbox';
      box.setAttribute('role', 'dialog');
      box.setAttribute('aria-label', 'Bildansicht');
      var img = document.createElement('img');
      img.src = a.getAttribute('href');
      img.alt = '';
      box.appendChild(img);
      box.addEventListener('click', function () { box.remove(); });
      document.addEventListener('keydown', function esc(ev) { if (ev.key === 'Escape') { box.remove(); document.removeEventListener('keydown', esc); } });
      document.body.appendChild(box);
      return;
    }
    // Bestätigung für gefährliche Aktionen
    var c = e.target.closest && e.target.closest('[data-confirm]');
    if (c && !confirm(c.getAttribute('data-confirm'))) { e.preventDefault(); e.stopPropagation(); }
  }, true);

  // Auswahllisten, die das Formular sofort absenden (Sortierung etc.)
  document.addEventListener('change', function (e) {
    var el = e.target;
    if (el && el.hasAttribute && el.hasAttribute('data-autosubmit') && el.form) el.form.requestSubmit ? el.form.requestSubmit() : el.form.submit();
  });

  // Editor: BBCode um die Auswahl legen
  window.vbf = {
    wrap: function (id, open, close) {
      var t = document.getElementById(id);
      if (!t) return null;
      var s = t.selectionStart, en = t.selectionEnd, v = t.value;
      var sel = v.substring(s, en);
      t.value = v.substring(0, s) + open + sel + close + v.substring(en);
      t.focus();
      var pos = sel.length ? s + open.length + sel.length + close.length : s + open.length;
      t.setSelectionRange(pos, pos);
      return t.value;
    },
    insert: function (id, text) {
      var t = document.getElementById(id);
      if (!t) return null;
      var s = t.selectionStart, v = t.value;
      t.value = v.substring(0, s) + text + v.substring(t.selectionEnd);
      t.focus();
      t.setSelectionRange(s + text.length, s + text.length);
      return t.value;
    },
    saveDraft: function (key, value) { try { localStorage.setItem('vbf-draft-' + key, value); } catch (e) { } },
    loadDraft: function (key) { try { return localStorage.getItem('vbf-draft-' + key); } catch (e) { return null; } },
    clearDraft: function (key) { try { localStorage.removeItem('vbf-draft-' + key); } catch (e) { } },
    scrollTo: function (id) { var el = document.getElementById(id); if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' }); }
  };

  // ---------- Popup bei neuen privaten Nachrichten ----------
  // Fragt (nur bei sichtbarem Tab) jede Minute den PN-Status ab. Jede Nachricht wird nur einmal gemeldet –
  // auch über mehrere offene Tabs hinweg (gemerkt in localStorage).
  var SEEN = 'vbf-pm-seen', timer = null, busy = false;
  function seen() { try { return parseInt(localStorage.getItem(SEEN) || '0', 10) || 0; } catch (e) { return 0; } }
  function markSeen(id) { try { if (id > seen()) localStorage.setItem(SEEN, String(id)); } catch (e) { } }

  function updateBadge(n) {
    var b = document.getElementById('pm-count'), l = document.getElementById('pm-link');
    if (b) { b.textContent = n; b.hidden = n === 0; }
    if (l) l.setAttribute('aria-label', 'Private Nachrichten' + (n > 0 ? ' (' + n + ' ungelesen)' : ''));
  }

  function closeToast() { var t = document.getElementById('pm-toast'); if (t) t.remove(); }

  function showToast(m, unread) {
    closeToast();
    var t = document.createElement('div');
    t.id = 'pm-toast'; t.className = 'toast'; t.setAttribute('role', 'status'); t.setAttribute('aria-live', 'polite');
    var head = document.createElement('div'); head.className = 'toast-head';
    head.textContent = unread > 1 ? unread + ' neue private Nachrichten' : 'Neue private Nachricht';
    var body = document.createElement('div'); body.className = 'toast-body';
    var from = document.createElement('strong'); from.textContent = m.from;
    body.appendChild(from); body.appendChild(document.createTextNode(': ' + m.title));
    var meta = document.createElement('div'); meta.className = 'toast-meta'; meta.textContent = m.time;
    var actions = document.createElement('div'); actions.className = 'toast-actions';
    var close = document.createElement('button'); close.type = 'button'; close.className = 'btn btn-sm btn-ghost'; close.textContent = 'Schließen';
    close.onclick = function () { markSeen(m.id); closeToast(); };
    var read = document.createElement('a'); read.className = 'btn btn-sm btn-primary'; read.href = '/account/pm/' + m.id; read.textContent = 'Lesen';
    read.onclick = function () { markSeen(m.id); };
    actions.appendChild(close); actions.appendChild(read);
    t.appendChild(head); t.appendChild(body); t.appendChild(meta); t.appendChild(actions);
    document.body.appendChild(t);
  }

  function poll() {
    if (busy || document.hidden || !document.querySelector('[data-pm-poll]')) return;
    busy = true;
    fetch('/api/pm/status', { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (s) {
        if (!s) return;
        updateBadge(s.unread);
        var onPmPage = location.pathname.indexOf('/account/pm') === 0;
        if (s.popup && s.latest && s.latest.id > seen() && !onPmPage) showToast(s.latest, s.unread);
        if (!s.latest) closeToast();
      })
      .catch(function () { })
      .then(function () { busy = false; });
  }

  function start() {
    if (timer || !document.querySelector('[data-pm-poll]')) return;
    timer = setInterval(poll, 60000);
    setTimeout(poll, 1500);
  }
  document.addEventListener('visibilitychange', function () { if (!document.hidden) poll(); });
  // Anderer Tab hat die Meldung geschlossen → hier auch schließen
  window.addEventListener('storage', function (e) { if (e.key === SEEN) closeToast(); });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
  // Nach erweiterter Navigation (Blazor) erneut prüfen
  window.addEventListener('load', function () {
    if (window.Blazor && Blazor.addEventListener) Blazor.addEventListener('enhancedload', function () { start(); poll(); });
  });
})();

// Benutzer-Aufklappmenü: bei Klick daneben oder Escape schließen
(function () {
  document.addEventListener('click', function (e) {
    document.querySelectorAll('details.user-menu[open]').forEach(function (d) { if (!d.contains(e.target)) d.removeAttribute('open'); });
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') document.querySelectorAll('details.user-menu[open]').forEach(function (d) { d.removeAttribute('open'); });
  });
})();

// Hell/Dunkel: sofort umschalten, im Hintergrund speichern (ohne JS: normales Formular mit Neuladen)
(function () {
  function effective() {
    var t = document.documentElement.getAttribute('data-theme');
    if (t === 'dark' || t === 'light') return t;
    return window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  document.addEventListener('submit', function (e) {
    var f = e.target;
    if (!f || !f.matches || !f.matches('form[data-theme-toggle]')) return;
    e.preventDefault();
    var next = effective() === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', next);
    var meta = document.querySelector('meta[name=color-scheme]'); if (meta) meta.setAttribute('content', next);
    f.querySelector('input[name=theme]').value = next;
    fetch(f.action, { method: 'POST', body: new FormData(f), credentials: 'same-origin', redirect: 'manual' }).catch(function () { });
  }, true);
})();
