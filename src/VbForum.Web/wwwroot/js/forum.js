// Kleine Helfer – kein Framework nötig.
(function () {
  // Bild-Lightbox für Anhänge: blättern mit Pfeiltasten, Buttons oder Wischen; alle Bilder der Seite in Reihenfolge
  document.addEventListener('click', function (e) {
    var a = e.target.closest && e.target.closest('a[data-lightbox]');
    if (a && !e.ctrlKey && !e.metaKey && !e.shiftKey && e.button === 0) {
      e.preventDefault();
      openLightbox(a);
      return;
    }
    // Bestätigung für gefährliche Aktionen
    var c = e.target.closest && e.target.closest('[data-confirm]');
    if (c && !confirm(c.getAttribute('data-confirm'))) { e.preventDefault(); e.stopPropagation(); }
  }, true);

  function openLightbox(start) {
    var links = Array.prototype.slice.call(document.querySelectorAll('a[data-lightbox]'));
    var seen = {}, list = [];
    links.forEach(function (l) { var h = l.getAttribute('href'); if (!seen[h]) { seen[h] = true; list.push(l); } });
    var idx = Math.max(0, list.findIndex(function (l) { return l.getAttribute('href') === start.getAttribute('href'); }));
    var old = document.querySelector('.lightbox'); if (old) old.remove();

    var box = document.createElement('div');
    box.className = 'lightbox';
    box.setAttribute('role', 'dialog');
    box.setAttribute('aria-modal', 'true');
    box.setAttribute('aria-label', 'Bildansicht');
    var img = document.createElement('img'); img.alt = '';
    var cap = document.createElement('div'); cap.className = 'lightbox-cap';
    function btn(cls, label, text) {
      var b = document.createElement('button'); b.type = 'button'; b.className = 'lightbox-btn ' + cls;
      b.setAttribute('aria-label', label); b.title = label; b.textContent = text; return b;
    }
    var prev = btn('lb-prev', 'Vorheriges Bild (←)', '‹'), next = btn('lb-next', 'Nächstes Bild (→)', '›'), close = btn('lb-close', 'Schließen (Esc)', '×');
    box.appendChild(img); box.appendChild(cap); box.appendChild(close);
    if (list.length > 1) { box.appendChild(prev); box.appendChild(next); }

    function show(i) {
      idx = (i + list.length) % list.length;
      var l = list[idx];
      img.src = l.getAttribute('href');
      var t = l.getAttribute('title') || (l.querySelector('img') && l.querySelector('img').getAttribute('alt')) || '';
      cap.textContent = (list.length > 1 ? (idx + 1) + ' / ' + list.length + (t ? ' · ' : '') : '') + t;
      cap.hidden = !cap.textContent;
      // Nachbarn vorladen
      [idx + 1, idx - 1].forEach(function (j) { var n = list[(j + list.length) % list.length]; if (n) { var p = new Image(); p.src = n.getAttribute('href'); } });
    }
    function shut() { box.remove(); document.removeEventListener('keydown', keys, true); if (start.focus) start.focus({ preventScroll: true }); }
    function keys(ev) {
      if (ev.key === 'Escape') { ev.preventDefault(); shut(); }
      else if (ev.key === 'ArrowRight' && list.length > 1) { ev.preventDefault(); show(idx + 1); }
      else if (ev.key === 'ArrowLeft' && list.length > 1) { ev.preventDefault(); show(idx - 1); }
    }
    prev.addEventListener('click', function (ev) { ev.stopPropagation(); show(idx - 1); });
    next.addEventListener('click', function (ev) { ev.stopPropagation(); show(idx + 1); });
    close.addEventListener('click', function (ev) { ev.stopPropagation(); shut(); });
    // Klick aufs Bild: weiter (bei mehreren), Klick daneben: schließen
    img.addEventListener('click', function (ev) { ev.stopPropagation(); if (list.length > 1) show(idx + 1); else shut(); });
    box.addEventListener('click', function () { shut(); });
    // Wischen auf Touch-Geräten
    var x0 = null, y0 = null;
    box.addEventListener('touchstart', function (ev) { if (ev.touches.length === 1) { x0 = ev.touches[0].clientX; y0 = ev.touches[0].clientY; } }, { passive: true });
    box.addEventListener('touchend', function (ev) {
      if (x0 === null || list.length < 2) return;
      var dx = ev.changedTouches[0].clientX - x0, dy = ev.changedTouches[0].clientY - y0; x0 = null;
      if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy)) show(idx + (dx < 0 ? 1 : -1));
    });
    document.addEventListener('keydown', keys, true);
    document.body.appendChild(box);
    show(idx);
    close.focus({ preventScroll: true });
  }

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

// Hinweisleiste wegklicken (Cookie 1 Jahr)
(function () {
  document.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-dismiss-notice]');
    if (!b) return;
    var key = b.getAttribute('data-dismiss-notice');
    document.cookie = key + '=1; max-age=31536000; path=/; samesite=lax' + (location.protocol === 'https:' ? '; secure' : '');
    var bar = b.closest('.notice-bar'); if (bar) bar.remove();
  });
})();

// Benutzer-Aufklappmenü: bei Klick daneben oder Escape schließen
(function () {
  document.addEventListener('click', function (e) {
    document.querySelectorAll('details.user-menu[open], details.mod-menu[open]').forEach(function (d) { if (!d.contains(e.target)) d.removeAttribute('open'); });
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') document.querySelectorAll('details.user-menu[open], details.mod-menu[open]').forEach(function (d) { d.removeAttribute('open'); });
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
    // Cookie sofort selbst setzen (1 Jahr) – unabhängig davon, ob die Anfrage an den Server durchgeht
    document.cookie = 'vbf-theme=' + next + '; max-age=31536000; path=/; samesite=lax' + (location.protocol === 'https:' ? '; secure' : '');
    fetch(f.action, { method: 'POST', body: new FormData(f), credentials: 'same-origin', redirect: 'manual' }).catch(function () { });
  }, true);
})();
