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
})();
