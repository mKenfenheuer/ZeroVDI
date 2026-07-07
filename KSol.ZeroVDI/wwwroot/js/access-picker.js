// Wires the shared _AccessPicker partials: typing in the filter input narrows the candidate <select>
// and auto-selects the first match, so granting access to one item out of many is fast. Progressive
// enhancement only — the picker is a working <select> + submit without this script.
(function () {
    function wire(form) {
        var filter = form.querySelector('[data-ap-filter]');
        var select = form.querySelector('[data-ap-select]');
        if (!filter || !select) return;

        // Snapshot the original options (skip the placeholder at index 0).
        var options = [].slice.call(select.options).filter(function (o) { return o.value !== ''; })
            .map(function (o) { return { value: o.value, label: o.textContent }; });

        function render(term) {
            term = (term || '').trim().toLowerCase();
            var first = select.options[0];
            select.innerHTML = '';
            select.appendChild(first);
            var firstMatchSelected = false;
            options.forEach(function (o) {
                if (term && o.label.toLowerCase().indexOf(term) === -1) return;
                var opt = document.createElement('option');
                opt.value = o.value;
                opt.textContent = o.label;
                if (term && !firstMatchSelected) { opt.selected = true; firstMatchSelected = true; }
                select.appendChild(opt);
            });
        }

        filter.addEventListener('input', function () { render(filter.value); });
        // Enter in the filter submits when a match is selected.
        filter.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && select.value) { e.preventDefault(); form.requestSubmit(); }
        });
    }

    document.querySelectorAll('[data-access-picker]').forEach(wire);
})();
