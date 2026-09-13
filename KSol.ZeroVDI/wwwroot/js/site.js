// Small behaviours shared by the admin pages. These live in a file rather than in inline handlers
// because the Content-Security-Policy has no 'unsafe-inline' for scripts and a nonce cannot cover an
// on* attribute — a nonce applies to elements, not attributes.

// A <select data-autosubmit> submits its form when changed: the filter dropdowns above the resource
// and audit lists.
document.addEventListener("change", function (e) {
    var el = e.target;
    if (el && el.matches && el.matches("select[data-autosubmit]") && el.form) el.form.submit();
});

// The appearance editor's colour picker mirrors its value into the hex label next to it.
document.addEventListener("input", function (e) {
    if (!e.target || e.target.id !== "hexInput") return;
    var label = document.getElementById("hexLabel");
    if (label) label.textContent = e.target.value;
});
