// Jellyfin Requests: the requests page injected into jellyfin-web.
// Renders inside Jellyfin's own page container with Jellyfin's classes, so server themes apply.
(function () {
  'use strict';

  if (window.__jellyfinRequests) return;
  window.__jellyfinRequests = true;

  const script = document.currentScript;
  const scriptUrl = script ? new URL(script.src, location.href) : null;
  const assetVersion = scriptUrl ? scriptUrl.searchParams.get('v') || '' : '';
  const ROUTE = '#/requests';
  const PAGE_ID = 'jfrRequestsPage';
  const PREFS_KEY = 'jellyfinRequests.prefs';
  const LOG = '[Jellyfin Requests]';

  const STATES = {
    pending: { label: 'Awaiting approval', icon: 'schedule', tone: 'warn', bucket: 'pending' },
    processing: { label: 'Searching', icon: 'travel_explore', tone: 'accent', bucket: 'progress' },
    downloading: { label: 'Downloading', icon: 'download', tone: 'accent', bucket: 'progress' },
    importing: { label: 'Importing', icon: 'move_to_inbox', tone: 'accent', bucket: 'progress' },
    partial: { label: 'Partially available', icon: 'incomplete_circle', tone: 'good', bucket: 'available' },
    available: { label: 'Available', icon: 'check_circle', tone: 'good', bucket: 'available' },
    declined: { label: 'Declined', icon: 'block', tone: 'bad', bucket: 'closed' },
    failed: { label: 'Failed', icon: 'error', tone: 'bad', bucket: 'closed' },
    blocklisted: { label: 'Blocklisted', icon: 'block', tone: 'muted', bucket: 'closed' },
    removed: { label: 'Removed', icon: 'delete', tone: 'muted', bucket: 'closed' },
  };

  const BUCKETS = [
    { id: 'all', label: 'All' },
    { id: 'pending', label: 'Pending' },
    { id: 'progress', label: 'In progress' },
    { id: 'available', label: 'Available' },
    { id: 'closed', label: 'Declined' },
  ];

  const DOWNLOAD_LABELS = { queued: 'Queued', paused: 'Paused', warning: 'Download issue', failed: 'Download failed' };

  const state = {
    label: (script && script.getAttribute('data-label')) || 'Requests',
    visible: false,
    data: null,
    version: 0,
    loading: false,
    polling: false,
    live: false,
    error: null,
    previousPage: null,
    busy: new Set(),
    prefs: loadPrefs(),
  };

  // ---------- utilities ----------

  function loadPrefs() {
    const defaults = { bucket: 'all', type: 'all', view: 'people', search: '', collapsed: {} };
    try {
      const saved = JSON.parse(localStorage.getItem(PREFS_KEY) || '{}');
      // Older versions stored two toggles; map them onto the single view switch.
      if (!saved.view && ('mine' in saved || 'grouped' in saved)) {
        saved.view = saved.mine ? 'mine' : saved.grouped === false ? 'all' : 'people';
      }
      delete saved.mine;
      delete saved.grouped;
      return Object.assign(defaults, saved, { search: '' });
    } catch (e) {
      return defaults;
    }
  }

  function savePrefs() {
    try {
      const { search, ...rest } = state.prefs;
      localStorage.setItem(PREFS_KEY, JSON.stringify(rest));
    } catch (e) { /* storage unavailable */ }
  }

  function esc(value) {
    return String(value == null ? '' : value).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  function api(method, path, query) {
    const client = window.ApiClient;
    const url = client.getUrl('JellyfinRequests/' + path, query);
    return method === 'GET'
      ? client.ajax({ type: 'GET', url, dataType: 'json' })
      : client.ajax({ type: method, url });
  }

  const rtf = typeof Intl !== 'undefined' && Intl.RelativeTimeFormat ? new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' }) : null;

  function relativeTime(iso) {
    const then = new Date(iso).getTime();
    if (!then) return '';
    const diff = (then - Date.now()) / 1000;
    const units = [['year', 31536000], ['month', 2592000], ['week', 604800], ['day', 86400], ['hour', 3600], ['minute', 60]];
    for (const [unit, secs] of units) {
      if (Math.abs(diff) >= secs) {
        const n = Math.round(diff / secs);
        return rtf ? rtf.format(n, unit) : `${Math.abs(n)} ${unit}${Math.abs(n) === 1 ? '' : 's'} ago`;
      }
    }
    return 'just now';
  }

  function timeLeft(iso) {
    const ms = new Date(iso).getTime() - Date.now();
    if (!ms || ms <= 0) return '';
    const mins = Math.round(ms / 60000);
    if (mins < 1) return 'under a minute left';
    if (mins < 60) return `${mins} min left`;
    const hours = Math.floor(mins / 60);
    if (hours < 48) return `${hours} h ${mins % 60} min left`;
    return `${Math.round(hours / 24)} days left`;
  }

  function seasonsText(seasons) {
    if (!seasons || !seasons.length) return '';
    if (seasons.length === 1) return `Season ${seasons[0]}`;
    const contiguous = seasons.every((s, i) => i === 0 || s === seasons[i - 1] + 1);
    return contiguous && seasons.length > 2
      ? `Seasons ${seasons[0]}–${seasons[seasons.length - 1]}`
      : `Seasons ${seasons.join(', ')}`;
  }

  function initials(name) {
    return (name || '?').split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0].toUpperCase()).join('');
  }

  // ---------- theme ----------

  // Reads the active theme's accent by measuring a throwaway Jellyfin primary button.
  function applyThemeAccent(root) {
    try {
      const probe = document.createElement('button');
      probe.className = 'raised button-submit emby-button';
      probe.style.cssText = 'position:absolute;visibility:hidden;pointer-events:none';
      root.appendChild(probe);
      const bg = getComputedStyle(probe).backgroundColor;
      const fg = getComputedStyle(probe).color;
      probe.remove();
      if (bg && !/rgba?\(0, 0, 0, 0\)|transparent/.test(bg)) {
        root.style.setProperty('--jfr-accent', bg);
        root.style.setProperty('--jfr-on-accent', fg);
      }
    } catch (e) { /* keep defaults */ }
  }

  // Dialog surface color from the theme's own .dialog style.
  function applyDialogTheme(el) {
    applyThemeAccent(el);
    try {
      const probe = document.createElement('div');
      probe.className = 'dialog';
      probe.style.cssText = 'position:absolute;visibility:hidden;pointer-events:none';
      el.appendChild(probe);
      const bg = getComputedStyle(probe).backgroundColor;
      probe.remove();
      if (bg && !/rgba?\(0, 0, 0, 0\)|transparent/.test(bg)) el.style.setProperty('--jfr-dialog-bg', bg);
    } catch (e) { /* keep defaults */ }
  }

  // ---------- dialog ----------

  // In-page replacement for window.confirm/alert. Resolves true when confirmed.
  function showDialog({ title, message, confirmLabel = 'OK', cancelLabel = null, danger = false }) {
    return new Promise((resolve) => {
      const previous = document.activeElement;
      const overlay = document.createElement('div');
      overlay.className = 'jfr-dialog-backdrop';
      overlay.innerHTML = `
        <div class="jfr-dialog" role="alertdialog" aria-modal="true" aria-labelledby="jfrDialogTitle" aria-describedby="jfrDialogMessage">
          <h3 id="jfrDialogTitle" class="jfr-dialog-title">${esc(title)}</h3>
          <p id="jfrDialogMessage" class="jfr-dialog-message">${esc(message)}</p>
          <div class="jfr-dialog-actions">
            ${cancelLabel ? `<button type="button" class="emby-button raised jfr-dialog-cancel">${esc(cancelLabel)}</button>` : ''}
            <button type="button" class="emby-button raised button-submit jfr-dialog-confirm${danger ? ' jfr-btn-danger' : ''}">${esc(confirmLabel)}</button>
          </div>
        </div>`;
      document.body.appendChild(overlay);
      applyDialogTheme(overlay);

      const buttons = [...overlay.querySelectorAll('button')];
      const onKey = (e) => {
        if (e.key === 'Escape') {
          e.preventDefault();
          e.stopPropagation();
          close(false);
        } else if (e.key === 'Tab') {
          // Keep focus inside the dialog.
          e.preventDefault();
          const i = buttons.indexOf(document.activeElement);
          buttons[(i + (e.shiftKey ? -1 : 1) + buttons.length) % buttons.length].focus();
        }
      };
      const close = (result) => {
        document.removeEventListener('keydown', onKey, true);
        overlay.remove();
        if (previous && previous.focus) previous.focus();
        resolve(result);
      };

      overlay.addEventListener('click', (e) => {
        if (e.target === overlay || e.target.closest('.jfr-dialog-cancel')) close(false);
        else if (e.target.closest('.jfr-dialog-confirm')) close(true);
      });
      document.addEventListener('keydown', onKey, true);

      // Destructive actions start on the safe choice.
      (overlay.querySelector(danger ? '.jfr-dialog-cancel' : '.jfr-dialog-confirm') || buttons[0]).focus();
    });
  }

  // ---------- page shell ----------

  function ensureStyles() {
    if (document.getElementById('jfrStyles') || !scriptUrl) return;
    const link = document.createElement('link');
    link.id = 'jfrStyles';
    link.rel = 'stylesheet';
    link.href = new URL('client.css' + (assetVersion ? '?v=' + encodeURIComponent(assetVersion) : ''), scriptUrl).href;
    document.head.appendChild(link);
  }

  function ensurePage() {
    let page = document.getElementById(PAGE_ID);
    if (page) return page;

    page = document.createElement('div');
    page.id = PAGE_ID;
    page.className = 'page type-interior mainAnimatedPage hide';
    page.setAttribute('data-title', state.label);
    page.setAttribute('data-backbutton', 'true');
    page.setAttribute('data-url', ROUTE);
    page.innerHTML = '<div data-role="content"><div class="content-primary jfr-root"></div></div>';

    placePage(page);

    const root = page.querySelector('.jfr-root');
    root.addEventListener('click', onClick);
    root.addEventListener('input', onInput);
    root.addEventListener('error', onImageError, true);
    applyThemeAccent(root);
    return page;
  }

  // Sits beside whichever page is showing. Jellyfin 12's default (MUI) layout keeps visible pages
  // outside the classic .mainAnimatedPages container, which stays in the DOM with zero height.
  function placePage(page) {
    const current = [...document.querySelectorAll('.mainAnimatedPage:not(.hide)')].find((p) => p !== page);
    const host = (current && current.parentElement) || document.querySelector('.mainAnimatedPages');
    if (host) {
      page.classList.remove('jfr-overlay');
      if (page.parentElement !== host) host.appendChild(page);
    } else if (!page.parentElement) {
      // Unknown layout: float over the app below the header.
      page.classList.add('jfr-overlay');
      document.body.appendChild(page);
    }
  }

  function setHeaderTitle() {
    const title = document.querySelector('.skinHeader .pageTitle');
    if (title) title.textContent = state.label;
    document.title = state.label;
  }

  function showPage(pushHistory) {
    if (!window.ApiClient || !window.ApiClient.getCurrentUserId()) return;
    ensureStyles();
    const page = ensurePage();
    placePage(page);

    if (pushHistory && location.hash !== ROUTE) {
      history.pushState({ jellyfinRequests: true }, '', ROUTE);
    }

    document.querySelectorAll('.mainAnimatedPage:not(.hide)').forEach((p) => {
      if (p !== page) {
        state.previousPage = p;
        p.classList.add('hide');
      }
    });

    const wasVisible = state.visible;
    page.classList.remove('hide');
    state.visible = true;
    if (!wasVisible) {
      page.dispatchEvent(new CustomEvent('viewshow', { bubbles: true, detail: { type: 'custom', isRestored: false, options: {} } }));
    }
    setHeaderTitle();
    // On a direct load the router titles its own 404 for our URL after we run.
    setTimeout(() => { if (state.visible) setHeaderTitle(); }, 500);
    syncNavSelection();

    if (!wasVisible) {
      render();
      load().then(pollLoop);
    }
  }

  function hidePage() {
    if (!state.visible) return;
    state.visible = false;
    const page = document.getElementById(PAGE_ID);
    if (page) page.classList.add('hide');
    syncNavSelection();

    // Back button: bring back the page we covered if the router didn't show another.
    if (state.previousPage && !document.querySelector(`.mainAnimatedPage:not(.hide):not(#${PAGE_ID})`)) {
      state.previousPage.classList.remove('hide');
    }
    state.previousPage = null;
  }

  // Jellyfin highlights the menu entry for its own current route and doesn't know about ours,
  // so while Requests is open the highlight moves here, and goes back on leaving.
  function syncNavSelection() {
    document.querySelectorAll('.jfr-nav-link').forEach((a) => {
      a.classList.toggle(a.classList.contains('MuiListItemButton-root') ? 'Mui-selected' : 'navMenuOption-selected', state.visible);
    });

    if (state.visible) {
      document.querySelectorAll('.MuiDrawer-paper .Mui-selected:not(.jfr-nav-link), .mainDrawer .navMenuOption-selected:not(.jfr-nav-link)').forEach((el) => {
        const cls = el.classList.contains('Mui-selected') ? 'Mui-selected' : 'navMenuOption-selected';
        el.classList.remove(cls);
        el.setAttribute('data-jfr-deselected', cls);
      });
    } else {
      document.querySelectorAll('[data-jfr-deselected]').forEach((el) => {
        const cls = el.getAttribute('data-jfr-deselected');
        el.removeAttribute('data-jfr-deselected');
        // Returning to the same route doesn't re-render the entry, so restore its highlight here.
        if (el.getAttribute('href') === location.hash) el.classList.add(cls);
      });
    }
  }

  function onLocationChange() {
    if (location.hash.indexOf(ROUTE) === 0) showPage(false);
    else hidePage();
  }

  // ---------- navigation entry points ----------

  // Material "playlist_add_check", for the MUI layout's SVG icons.
  const ICON_PATH = 'M14 10H3v2h11zm0-4H3v2h11zM3 16h7v-2H3zm18.5-4.5L23 13l-6.99 7-4.51-4.5L13 14l3.01 3z';

  function setSvgIcon(el) {
    const svg = el.querySelector('svg');
    if (!svg) return;
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.removeAttribute('data-testid');
    svg.innerHTML = `<path d="${ICON_PATH}"></path>`;
  }

  function injectNav() {
    if (!window.ApiClient || !window.ApiClient.getCurrentUserId()) return;
    injectMuiNav();
    injectClassicNav();
  }

  // Jellyfin 12's default layout. Entries are cloned from Jellyfin's own so theme styling carries over.
  function injectMuiNav() {
    // The drawer list only exists while the drawer is open.
    const homeLink = document.querySelector('.MuiDrawer-paper ul.MuiList-root a[href="#/home"]');
    const list = homeLink && homeLink.closest('ul');
    if (list && !list.querySelector('.jfr-nav-link')) {
      const template = homeLink.closest('li');
      const item = template.cloneNode(true);
      const link = item.querySelector('a');
      link.classList.add('jfr-nav-link');
      link.classList.remove('Mui-selected');
      link.removeAttribute('aria-current');
      link.href = ROUTE;
      setSvgIcon(link);
      const text = link.querySelector('.MuiListItemText-primary');
      if (text) text.textContent = state.label;
      // No explicit close: the drawer list closes itself on any click inside it (it toggles,
      // so closing it again here would reopen it).
      link.addEventListener('click', (e) => {
        e.preventDefault();
        showPage(true);
      });
      template.parentNode.insertBefore(item, template.nextSibling);
    }

    // Wide screens show text links (Favorites) in the app bar; add ours beside them.
    const favorites = document.querySelector('header.MuiAppBar-root a.MuiButton-root[href="#/home?tab=1"]');
    if (favorites && !favorites.parentNode.querySelector('.jfr-appbar-link')) {
      const link = favorites.cloneNode(true);
      link.classList.add('jfr-appbar-link');
      link.href = ROUTE;
      setSvgIcon(link);
      const icon = link.querySelector('.MuiButton-startIcon');
      [...link.childNodes].forEach((n) => { if (n !== icon) n.remove(); });
      link.appendChild(document.createTextNode(state.label));
      link.addEventListener('click', (e) => {
        e.preventDefault();
        showPage(true);
      });
      favorites.parentNode.insertBefore(link, favorites.nextSibling);
    }

    const toolbar = document.querySelector('header.MuiAppBar-root .MuiToolbar-root');
    if (toolbar && !toolbar.querySelector('.jfr-header-button')) {
      // The first icon button opens the menu; the rest sit on the right (SyncPlay, cast, user).
      const right = [...toolbar.querySelectorAll('button.MuiIconButton-root')].slice(1).find((b) => b.querySelector('svg'));
      if (right) {
        const button = right.cloneNode(true);
        ['aria-controls', 'aria-haspopup', 'aria-expanded', 'id'].forEach((a) => button.removeAttribute(a));
        button.classList.add('jfr-header-button');
        button.title = state.label;
        button.setAttribute('aria-label', state.label);
        setSvgIcon(button);
        button.addEventListener('click', () => showPage(true));
        right.parentNode.insertBefore(button, right);
      }
    }
  }

  // Classic layout (still selectable in Jellyfin's display settings).
  function injectClassicNav() {
    const drawer = document.querySelector('.mainDrawer-scrollContainer');
    if (drawer && !drawer.querySelector('.jfr-nav-link')) {
      const link = document.createElement('a');
      link.setAttribute('is', 'emby-linkbutton');
      link.className = 'navMenuOption lnkMediaFolder emby-button jfr-nav-link';
      link.href = ROUTE;
      link.innerHTML = `<span class="material-icons navMenuOptionIcon" aria-hidden="true">playlist_add_check</span><span class="sectionName navMenuOptionText">${esc(state.label)}</span>`;
      link.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelector('.mainDrawer')?.classList.remove('drawer-open');
        showPage(true);
      });
      const home = drawer.querySelector('a.navMenuOption[href*="home"]');
      if (home && home.parentNode) home.parentNode.insertBefore(link, home.nextSibling);
      else drawer.prepend(link);
    }

    const headerRight = document.querySelector('.skinHeader .headerRight');
    if (headerRight && !headerRight.querySelector('.jfr-header-button')) {
      const button = document.createElement('button');
      button.type = 'button';
      button.setAttribute('is', 'paper-icon-button-light');
      button.className = 'headerButton headerButtonRight paper-icon-button-light jfr-header-button';
      button.title = state.label;
      button.setAttribute('aria-label', state.label);
      button.innerHTML = '<span class="material-icons" aria-hidden="true">playlist_add_check</span>';
      button.addEventListener('click', () => showPage(true));
      headerRight.prepend(button);
    }
  }

  // ---------- data ----------

  async function load() {
    if (state.loading) return;
    state.loading = true;
    try {
      const data = await api('GET', 'Requests');
      state.data = data;
      state.version = data.version;
      state.error = null;
      state.live = true;
      if (data.label && data.label !== state.label) {
        state.label = data.label;
        if (state.visible) setHeaderTitle();
      }
    } catch (e) {
      console.warn(LOG, 'load failed', e);
      state.error = 'Could not load requests. Retrying…';
      state.live = false;
    } finally {
      state.loading = false;
      if (state.visible) render();
    }
  }

  // Long-poll: the server answers as soon as anything changes, so updates land within a second or two.
  async function pollLoop() {
    if (state.polling) return;
    state.polling = true;
    try {
      while (state.visible) {
        if (document.hidden) {
          await sleep(1000);
          continue;
        }
        try {
          const res = await api('GET', 'Changes', { since: state.version });
          setLive(true);
          if (state.visible && res && res.version !== state.version) await load();
        } catch (e) {
          setLive(false);
          await sleep(5000);
        }
      }
    } finally {
      state.polling = false;
    }
  }

  function setLive(live) {
    if (state.live === live) return;
    state.live = live;
    const dot = document.querySelector('#' + PAGE_ID + ' .jfr-live');
    if (dot) dot.classList.toggle('jfr-live-off', !live);
  }

  async function act(action, id, confirmOptions) {
    if (confirmOptions && !(await showDialog(confirmOptions))) return;
    state.busy.add(id);
    render();
    try {
      await api('POST', `Requests/${id}/${action}`);
      await load();
    } catch (e) {
      console.warn(LOG, action, 'failed', e);
      const verb = { Approve: 'approve', Decline: 'decline', Cancel: 'cancel', Remove: 'remove' }[action] || 'update';
      await showDialog({ title: 'Something went wrong', message: `Could not ${verb} the request. Check that Seerr is reachable and try again.` });
    } finally {
      state.busy.delete(id);
      render();
    }
  }

  // ---------- filtering ----------

  function baseFilter(items) {
    const { type, view, search } = state.prefs;
    const mine = view === 'mine';
    const me = state.data && state.data.currentUserId;
    const q = search.trim().toLowerCase();
    return items.filter((it) =>
      (type === 'all' || it.type === type) &&
      (!mine || it.requestedBy.jellyfinUserId === me) &&
      (!q || it.title.toLowerCase().includes(q) || it.requestedBy.name.toLowerCase().includes(q)));
  }

  function bucketOf(item) {
    return (STATES[item.state] || STATES.processing).bucket;
  }

  // ---------- rendering ----------

  function render() {
    const page = document.getElementById(PAGE_ID);
    if (!page) return;
    const root = page.querySelector('.jfr-root');
    const active = document.activeElement;
    const searchFocused = active && active.classList && active.classList.contains('jfr-search');
    const caret = searchFocused ? active.selectionStart : null;

    root.innerHTML = renderToolbar() + renderBody();

    if (searchFocused) {
      const input = root.querySelector('.jfr-search');
      if (input) {
        input.focus();
        if (caret != null) input.setSelectionRange(caret, caret);
      }
    }
  }

  function renderToolbar() {
    const data = state.data;
    const items = data ? baseFilter(data.items) : [];
    const counts = { all: items.length };
    items.forEach((it) => { const b = bucketOf(it); counts[b] = (counts[b] || 0) + 1; });
    const p = state.prefs;

    const chips = BUCKETS
      .filter((b) => b.id === 'all' || b.id === p.bucket || counts[b.id])
      .map((b) => `<button type="button" class="jfr-chip" data-action="bucket" data-value="${b.id}" aria-pressed="${p.bucket === b.id}">${esc(b.label)}<span class="jfr-chip-count">${counts[b.id] || 0}</span></button>`)
      .join('');

    const typeOptions = [['all', 'Movies & shows'], ['movie', 'Movies'], ['tv', 'Shows']]
      .map(([v, l]) => `<option value="${v}"${p.type === v ? ' selected' : ''}>${l}</option>`).join('');

    const views = [['people', 'group', 'By person'], ['all', 'view_list', 'All'], ['mine', 'person', 'Mine']]
      .map(([v, icon, l]) => `<button type="button" role="radio" class="jfr-segment" data-action="view" data-value="${v}" aria-checked="${p.view === v}"><span class="material-icons" aria-hidden="true">${icon}</span>${l}</button>`)
      .join('');

    return `
      <div class="jfr-toolbar">
        <div class="jfr-title-row">
          <h2 class="sectionTitle jfr-title">${esc(state.label)}</h2>
          <span class="jfr-live${state.live ? '' : ' jfr-live-off'}" title="Live updates"><span class="jfr-live-dot"></span>Live</span>
          ${data && data.seerrUrl ? `<a class="jfr-seerr-link emby-button" href="${esc(data.seerrUrl)}" target="_blank" rel="noopener"><span class="material-icons" aria-hidden="true">add</span>New request</a>` : ''}
        </div>
        <div class="jfr-chips" role="group" aria-label="Status filter">${chips}</div>
        <div class="jfr-controls">
          <div class="jfr-search-wrap">
            <span class="material-icons" aria-hidden="true">search</span>
            <input class="jfr-search emby-input" type="search" placeholder="Search titles or people" value="${esc(p.search)}" aria-label="Search">
          </div>
          <select class="jfr-select emby-select" data-action="type" aria-label="Type">${typeOptions}</select>
          <div class="jfr-segmented" role="radiogroup" aria-label="View">${views}</div>
        </div>
      </div>`;
  }

  function renderBody() {
    const data = state.data;
    let banner = '';
    if (state.error) banner = `<div class="jfr-banner jfr-banner-bad">${esc(state.error)}</div>`;
    else if (data && data.warnings && data.warnings.length) banner = data.warnings.map((w) => `<div class="jfr-banner">${esc(w)}</div>`).join('');

    if (!data) {
      return banner + (state.error ? '' : '<div class="jfr-empty"><div class="jfr-spinner"></div></div>');
    }

    const p = state.prefs;
    const items = baseFilter(data.items)
      .filter((it) => p.bucket === 'all' || bucketOf(it) === p.bucket)
      .sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));

    if (!items.length) {
      const anything = data.items.length > 0;
      return banner + `
        <div class="jfr-empty">
          <span class="material-icons jfr-empty-icon" aria-hidden="true">${anything ? 'filter_alt_off' : 'movie_filter'}</span>
          <div>${anything ? 'Nothing matches these filters.' : 'No requests yet.'}</div>
          ${!anything && data.seerrUrl ? `<a class="emby-button raised button-submit jfr-empty-cta" href="${esc(data.seerrUrl)}" target="_blank" rel="noopener">Request something</a>` : ''}
        </div>`;
    }

    if (p.view !== 'people') {
      return banner + `<div class="jfr-grid">${items.map((it) => renderCard(it, p.view === 'all')).join('')}</div>`;
    }

    const me = data.currentUserId;
    const groups = new Map();
    items.forEach((it) => {
      const key = it.requestedBy.key;
      if (!groups.has(key)) groups.set(key, { user: it.requestedBy, items: [] });
      groups.get(key).items.push(it);
    });
    // You first, then whoever requested most recently.
    const ordered = [...groups.values()].sort((a, b) =>
      (b.user.jellyfinUserId === me) - (a.user.jellyfinUserId === me) ||
      new Date(b.items[0].createdAt) - new Date(a.items[0].createdAt));

    return banner + ordered.map((g) => renderGroup(g, me)).join('');
  }

  function renderGroup(group, me) {
    const { user, items } = group;
    const collapsed = !!state.prefs.collapsed[user.key];
    const inProgress = items.filter((it) => bucketOf(it) === 'progress').length;
    const pending = items.filter((it) => it.state === 'pending').length;
    const summary = [`${items.length} request${items.length === 1 ? '' : 's'}`];
    if (inProgress) summary.push(`${inProgress} in progress`);
    if (pending) summary.push(`${pending} pending`);

    const avatar = user.jellyfinUserId
      ? `<img class="jfr-avatar" alt="" loading="lazy" src="${esc(window.ApiClient.getUrl(`Users/${user.jellyfinUserId}/Images/Primary`, { height: 96, quality: 90 }))}" data-initials="${esc(initials(user.name))}">`
      : `<span class="jfr-avatar jfr-avatar-initials">${esc(initials(user.name))}</span>`;

    return `
      <section class="verticalSection jfr-group${collapsed ? ' jfr-collapsed' : ''}">
        <button type="button" class="jfr-group-header" data-action="collapse" data-value="${esc(user.key)}" aria-expanded="${!collapsed}">
          ${avatar}
          <span class="jfr-group-name">${esc(user.name)}${user.jellyfinUserId && user.jellyfinUserId === me ? ' <span class="jfr-you">you</span>' : ''}</span>
          <span class="jfr-group-summary">${esc(summary.join(' · '))}</span>
          <span class="material-icons jfr-chevron" aria-hidden="true">expand_more</span>
        </button>
        ${collapsed ? '' : `<div class="jfr-grid">${items.map((it) => renderCard(it, false)).join('')}</div>`}
      </section>`;
  }

  function posterUrl(it) {
    if (it.jellyfinItemId) {
      return window.ApiClient.getUrl(`Items/${it.jellyfinItemId}/Images/Primary`, { fillHeight: 270, fillWidth: 180, quality: 90 });
    }
    if (it.posterPath && it.posterPath.charAt(0) === '/') {
      return 'https://image.tmdb.org/t/p/w185' + it.posterPath;
    }
    return null;
  }

  // Jellyfin Enhanced's Seerr details modal. Missing when JE isn't installed, its Seerr
  // integration is off, or its module hasn't loaded yet.
  function jeSeerrDetails() {
    const je = window.JellyfinEnhanced && window.JellyfinEnhanced.jellyseerrMoreInfo;
    return je && typeof je.open === 'function' ? je : null;
  }

  function seerrDetailsAttrs(it) {
    return `data-action="seerr" data-value="${it.tmdbId}" data-type="${it.type === 'tv' ? 'tv' : 'movie'}"`;
  }

  function seerrItemUrl(it) {
    const base = state.data && state.data.seerrUrl;
    return base ? `${base}/${it.type === 'tv' ? 'tv' : 'movie'}/${it.tmdbId}` : null;
  }

  function renderStatus(it) {
    const s = STATES[it.state] || STATES.processing;
    const dl = it.download;
    let label = s.label;
    let detail = '';

    if (it.state === 'downloading' && dl) {
      label = DOWNLOAD_LABELS[dl.status] || `Downloading ${Math.round(dl.progress * 100)}%`;
      const parts = [];
      if (dl.eta && dl.status === 'downloading') parts.push(timeLeft(dl.eta));
      if (dl.items > 1) parts.push(`${dl.items} ${it.type === 'tv' ? 'episodes' : 'files'}`);
      detail = parts.filter(Boolean).join(' · ');
    } else if (it.state === 'partial' && dl) {
      label = `Partially available · downloading ${Math.round(dl.progress * 100)}%`;
    }

    const tone = dl && (dl.status === 'warning' || dl.status === 'failed') ? 'bad' : s.tone;
    const bar = dl && (it.state === 'downloading' || it.state === 'importing' || it.state === 'partial')
      ? `<div class="jfr-progress${dl.status === 'paused' || dl.status === 'queued' ? ' jfr-progress-idle' : ''}" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${Math.round(dl.progress * 100)}"><span style="width:${(dl.progress * 100).toFixed(1)}%"></span></div>`
      : it.state === 'processing' || it.state === 'importing' ? '<div class="jfr-progress jfr-progress-indeterminate"><span></span></div>' : '';

    return `
      <div class="jfr-status jfr-tone-${tone}">
        <span class="material-icons" aria-hidden="true">${s.icon}</span>
        <span class="jfr-status-label">${esc(label)}</span>
        ${detail ? `<span class="jfr-status-detail">${esc(detail)}</span>` : ''}
      </div>
      ${bar}`;
  }

  function renderActions(it) {
    const data = state.data;
    const me = data.currentUserId;
    const mine = it.requestedBy.jellyfinUserId && it.requestedBy.jellyfinUserId === me;
    const busy = state.busy.has(it.id);
    const disabled = busy ? ' disabled' : '';
    const buttons = [];

    if (it.jellyfinItemId && (it.state === 'available' || it.state === 'partial')) {
      buttons.push(`<button type="button" class="emby-button raised button-submit jfr-btn" data-action="open" data-value="${esc(it.jellyfinItemId)}"><span class="material-icons" aria-hidden="true">play_arrow</span>Watch</button>`);
    }
    if (data.isAdmin && it.state === 'pending') {
      buttons.push(`<button type="button" class="emby-button raised button-submit jfr-btn" data-action="Approve" data-value="${it.id}"${disabled}><span class="material-icons" aria-hidden="true">check</span>Approve</button>`);
      buttons.push(`<button type="button" class="emby-button raised jfr-btn" data-action="Decline" data-value="${it.id}"${disabled}><span class="material-icons" aria-hidden="true">close</span>Decline</button>`);
    }
    if (it.state === 'pending' && mine && data.canCancelOwn && !data.isAdmin) {
      buttons.push(`<button type="button" class="emby-button raised jfr-btn" data-action="Cancel" data-value="${it.id}"${disabled}>Cancel request</button>`);
    } else if (data.isAdmin && it.state !== 'pending') {
      buttons.push(`<button type="button" class="emby-button jfr-btn jfr-btn-quiet" data-action="Remove" data-value="${it.id}" title="Remove the request and delete the title from Radarr/Sonarr"${disabled}><span class="material-icons" aria-hidden="true">delete</span>Remove</button>`);
    }
    const seerr = seerrItemUrl(it);
    const details = seerrDetailsAttrs(it);
    if (jeSeerrDetails()) {
      // The href stays so Ctrl/middle-click still opens Seerr in a new tab.
      buttons.push(`<a class="emby-button jfr-btn jfr-btn-quiet" ${seerr ? `href="${esc(seerr)}" target="_blank" rel="noopener"` : 'href="#"'} ${details} title="More info"><span class="material-icons" aria-hidden="true">info</span></a>`);
    } else if (seerr) {
      buttons.push(`<a class="emby-button jfr-btn jfr-btn-quiet" href="${esc(seerr)}" target="_blank" rel="noopener" ${details} title="Open in Seerr"><span class="material-icons" aria-hidden="true">open_in_new</span></a>`);
    }
    if (busy) buttons.push('<span class="jfr-spinner jfr-spinner-small"></span>');

    return buttons.length ? `<div class="jfr-actions">${buttons.join('')}</div>` : '';
  }

  function renderCard(it, showRequester) {
    const poster = posterUrl(it);
    const meta = [
      it.type === 'tv' ? 'Show' : 'Movie',
      seasonsText(it.seasons),
      `requested ${relativeTime(it.createdAt)}`,
    ].filter(Boolean);
    // In the library: open Jellyfin's item page. Otherwise: Jellyfin Enhanced's Seerr details, if present.
    const target = it.jellyfinItemId
      ? `data-action="open" data-value="${esc(it.jellyfinItemId)}"`
      : jeSeerrDetails() ? seerrDetailsAttrs(it) : '';

    return `
      <article class="jfr-card jfr-state-${esc(it.state)}">
        <div class="jfr-poster" ${target}>
          ${poster ? `<img src="${esc(poster)}" alt="" loading="lazy">` : `<span class="material-icons" aria-hidden="true">${it.type === 'tv' ? 'tv' : 'movie'}</span>`}
        </div>
        <div class="jfr-card-body">
          <div class="jfr-card-title">
            <span class="jfr-name" ${target}>${esc(it.title)}</span>${it.year ? `<span class="jfr-year">${it.year}</span>` : ''}${it.is4k ? '<span class="jfr-badge">4K</span>' : ''}
          </div>
          <div class="jfr-meta">${esc(meta.join(' · '))}${showRequester ? ` · <strong>${esc(it.requestedBy.name)}</strong>` : ''}</div>
          ${renderStatus(it)}
          ${renderActions(it)}
        </div>
      </article>`;
  }

  // ---------- events ----------

  // Mirrors the server's RemovalPlanner so the dialog states what Remove will actually do.
  function removalDialog(it) {
    const app = it.type === 'tv' ? 'Sonarr' : 'Radarr';
    const sharing = state.data.items.filter((o) =>
      o.id !== it.id && o.mediaId === it.mediaId && o.is4k === it.is4k &&
      !['declined', 'failed', 'blocklisted'].includes(o.state));
    const names = [...new Set(sharing.map((o) => o.requestedBy.name))].join(', ');

    if (it.state === 'declined' || it.state === 'failed') {
      return {
        title: 'Remove this request?',
        message: `${it.requestedBy.name}'s ${it.state} request for ${it.title} will be deleted from Seerr. It never reached ${app}, so nothing else changes.`,
        confirmLabel: 'Remove',
        cancelLabel: 'Keep it',
        danger: true,
      };
    }
    if (sharing.length) {
      return {
        title: 'Remove this request?',
        message: `${names} also requested ${it.title}, so it stays in ${app} and the library. Only ${it.requestedBy.name}'s request is removed.`,
        confirmLabel: 'Remove request',
        cancelLabel: 'Keep it',
        danger: true,
      };
    }
    return {
      title: `Delete ${it.title}?`,
      message: `${it.title} will be deleted from ${app} along with its files on disk, and ${it.requestedBy.name}'s request removed. It will disappear from the library. This can't be undone.`,
      confirmLabel: 'Delete with files',
      cancelLabel: 'Keep it',
      danger: true,
    };
  }

  function findItem(id) {
    return state.data.items.find((it) => it.id === Number(id)) || { title: 'this title', requestedBy: { name: 'This user' } };
  }

  function onClick(e) {
    const el = e.target.closest('[data-action]');
    if (!el || el.disabled) return;
    const action = el.getAttribute('data-action');
    const value = el.getAttribute('data-value');
    const p = state.prefs;

    switch (action) {
      case 'bucket': p.bucket = value; break;
      case 'view': p.view = value; break;
      case 'collapse':
        if (p.collapsed[value]) delete p.collapsed[value];
        else p.collapsed[value] = true;
        break;
      case 'seerr': {
        const je = jeSeerrDetails();
        const newTab = e.ctrlKey || e.metaKey || e.shiftKey || e.button === 1;
        if (je && !newTab) {
          e.preventDefault();
          je.open(Number(value), el.getAttribute('data-type'));
        } else if (!el.getAttribute('href') || el.getAttribute('href') === '#') {
          // Poster/title, or a JE-only button: fall back to Seerr in a new tab.
          e.preventDefault();
          const url = seerrItemUrl({ type: el.getAttribute('data-type'), tmdbId: value });
          if (url) window.open(url, '_blank', 'noopener');
        }
        return;
      }
      case 'open':
        e.preventDefault();
        hidePage();
        if (window.Emby && window.Emby.Page && window.Emby.Page.showItem) window.Emby.Page.showItem(value);
        else location.hash = `#/details?id=${encodeURIComponent(value)}&serverId=${encodeURIComponent(window.ApiClient.serverId())}`;
        return;
      case 'Approve':
        act('Approve', Number(value));
        return;
      case 'Decline': {
        const it = findItem(value);
        act('Decline', Number(value), {
          title: 'Decline this request?',
          message: `${it.requestedBy.name}'s request for ${it.title} will be declined in Seerr.`,
          confirmLabel: 'Decline',
          cancelLabel: 'Keep it',
          danger: true,
        });
        return;
      }
      case 'Cancel': {
        const it = findItem(value);
        act('Cancel', Number(value), {
          title: 'Cancel your request?',
          message: `Your request for ${it.title} will be withdrawn.`,
          confirmLabel: 'Cancel request',
          cancelLabel: 'Keep it',
          danger: true,
        });
        return;
      }
      case 'Remove':
        act('Remove', Number(value), removalDialog(findItem(value)));
        return;
      default:
        return;
    }
    savePrefs();
    render();
  }

  // Missing avatars become initials; missing posters fall back to the type icon.
  function onImageError(e) {
    const img = e.target;
    if (!img || img.tagName !== 'IMG') return;
    const fallback = document.createElement('span');
    if (img.classList.contains('jfr-avatar')) {
      fallback.className = 'jfr-avatar jfr-avatar-initials';
      fallback.textContent = img.getAttribute('data-initials') || '?';
    } else {
      fallback.className = 'material-icons';
      fallback.setAttribute('aria-hidden', 'true');
      fallback.textContent = 'image_not_supported';
    }
    img.replaceWith(fallback);
  }

  function onInput(e) {
    if (e.target.classList.contains('jfr-search')) {
      state.prefs.search = e.target.value;
      render();
    } else if (e.target.getAttribute('data-action') === 'type') {
      state.prefs.type = e.target.value;
      savePrefs();
      render();
    }
  }

  // ---------- boot ----------

  function boot() {
    // Loaded up front so the dialog is styled the first time it opens (also on the settings page).
    ensureStyles();
    window.addEventListener('hashchange', onLocationChange);
    window.addEventListener('popstate', onLocationChange);

    // Jellyfin 12's router navigates with pushState, which fires no event; watch it directly.
    ['pushState', 'replaceState'].forEach((name) => {
      const original = history[name];
      history[name] = function () {
        const result = original.apply(this, arguments);
        setTimeout(onLocationChange, 0);
        return result;
      };
    });

    document.addEventListener('viewshow', (e) => {
      const target = e.target;
      if (!target || target.id === PAGE_ID) return;
      if (location.hash.indexOf(ROUTE) === 0) {
        // The router rendered its own page for our URL (e.g. a fresh load); keep ours on top.
        if (target.classList) target.classList.add('hide');
        showPage(false);
      } else {
        hidePage();
      }
    });

    document.addEventListener('visibilitychange', () => {
      if (!document.hidden && state.visible) load();
    });

    // The drawer and header are rebuilt on login and user switch; re-add our entry points.
    let queued = false;
    new MutationObserver(() => {
      // The drawer renders fresh each time it opens; fix its highlight before it paints.
      if (state.visible) syncNavSelection();
      if (queued) return;
      queued = true;
      setTimeout(() => { queued = false; injectNav(); }, 300);
    }).observe(document.body, { childList: true, subtree: true });

    const wait = setInterval(() => {
      if (!window.ApiClient) return;
      clearInterval(wait);
      injectNav();
      onLocationChange();
    }, 250);
  }

  try {
    boot();
  } catch (e) {
    console.error(LOG, 'failed to start', e);
  }

  // Exposed for debugging from the console.
  // dialog() is also used by the plugin's settings page.
  window.JellyfinRequests = {
    show: () => showPage(true),
    hide: hidePage,
    reload: load,
    dialog: (options) => {
      ensureStyles();
      return showDialog(options);
    },
    state,
  };
})();
