(function () {
    const key = 'qmstech-theme';
    const root = document.documentElement;
    const saved = localStorage.getItem(key);
    const preferred = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    const theme = saved || preferred;

    root.setAttribute('data-theme', theme);

    document.querySelectorAll('[data-theme-toggle]').forEach(button => {
        button.addEventListener('click', () => {
            const next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
            root.setAttribute('data-theme', next);
            localStorage.setItem(key, next);
        });
    });
})();
