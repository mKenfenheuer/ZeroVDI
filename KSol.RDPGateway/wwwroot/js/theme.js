// ============================================
// Theme system (ported from Cleopatra)
// Applies a color theme (data-theme) + light/dark mode (.dark), persisted in localStorage.
// The early <head> snippet (see _ThemeHead partial) applies the saved values before paint to
// avoid a flash of the wrong theme; this file wires up the interactive switcher.
// ============================================
(function () {
    'use strict';

    var THEME_KEY = 'rdpgw-theme';
    var MODE_KEY = 'rdpgw-mode';

    function setColorTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        localStorage.setItem(THEME_KEY, theme);
        updateColorButtons(theme);
    }

    function setMode(mode) {
        document.documentElement.classList.toggle('dark', mode === 'dark');
        localStorage.setItem(MODE_KEY, mode);
        updateModeButtons(mode);
    }

    function updateColorButtons(active) {
        document.querySelectorAll('.theme-color-btn').forEach(function (btn) {
            var on = btn.dataset.theme === active;
            btn.classList.toggle('ring-foreground', on);
            btn.classList.toggle('ring-transparent', !on);
        });
    }

    function updateModeButtons(active) {
        document.querySelectorAll('.theme-mode-btn').forEach(function (btn) {
            var on = btn.dataset.mode === active;
            btn.classList.toggle('bg-muted', on);
            btn.classList.toggle('text-foreground', on);
            btn.classList.toggle('text-muted-foreground', !on);
        });
    }

    function closeOnOutside(containerId, dropdownId) {
        document.addEventListener('click', function (e) {
            var container = document.getElementById(containerId);
            var dropdown = document.getElementById(dropdownId);
            if (container && dropdown && !container.contains(e.target)) {
                dropdown.classList.add('hidden');
            }
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                var dropdown = document.getElementById(dropdownId);
                if (dropdown) dropdown.classList.add('hidden');
            }
        });
    }

    function toggleDropdown(btnId, dropdownId, others) {
        var btn = document.getElementById(btnId);
        var dropdown = document.getElementById(dropdownId);
        if (!btn || !dropdown) return;
        btn.addEventListener('click', function (e) {
            e.stopPropagation();
            dropdown.classList.toggle('hidden');
            (others || []).forEach(function (id) {
                var d = document.getElementById(id);
                if (d) d.classList.add('hidden');
            });
        });
        closeOnOutside(btnId.replace('-btn', '-container'), dropdownId);
    }

    function init() {
        // Reflect persisted values onto the controls (mode/theme already applied pre-paint).
        var savedTheme = localStorage.getItem(THEME_KEY) || 'blue';
        var savedMode = localStorage.getItem(MODE_KEY) || 'light';
        updateColorButtons(savedTheme);
        updateModeButtons(savedMode);

        document.querySelectorAll('.theme-color-btn').forEach(function (btn) {
            btn.addEventListener('click', function () { setColorTheme(btn.dataset.theme); });
        });
        document.querySelectorAll('.theme-mode-btn').forEach(function (btn) {
            btn.addEventListener('click', function () { setMode(btn.dataset.mode); });
        });

        toggleDropdown('theme-toggle-btn', 'theme-dropdown', ['user-dropdown']);
        toggleDropdown('user-avatar-btn', 'user-dropdown', ['theme-dropdown']);

        // Mobile sidebar drawer (admin layout).
        var sidebarToggle = document.getElementById('mobile-menu-toggle');
        var sidebar = document.getElementById('sidebar');
        var overlay = document.getElementById('sidebar-overlay');
        function openSidebar() {
            if (!sidebar) return;
            sidebar.classList.remove('-translate-x-full');
            if (overlay) { overlay.classList.remove('hidden'); requestAnimationFrame(function () { overlay.classList.remove('opacity-0'); }); }
        }
        function closeSidebar() {
            if (!sidebar) return;
            sidebar.classList.add('-translate-x-full');
            if (overlay) { overlay.classList.add('opacity-0'); setTimeout(function () { overlay.classList.add('hidden'); }, 300); }
        }
        if (sidebarToggle) sidebarToggle.addEventListener('click', openSidebar);
        if (overlay) overlay.addEventListener('click', closeSidebar);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    // Expose minimal API for other scripts (e.g. quick dark toggle).
    window.RdpgwTheme = { setColorTheme: setColorTheme, setMode: setMode };
})();
