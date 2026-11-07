// Offline enhancement: works directly from file:// without a web server.
const search = document.querySelector('[data-search]');
if (search) search.addEventListener('input', () => {
  const q = search.value.toLocaleLowerCase().trim();
  document.querySelectorAll('[data-card]').forEach(c => c.hidden = !c.textContent.toLocaleLowerCase().includes(q));
});
document.querySelectorAll('[data-toggle]').forEach(b => b.addEventListener('click', () => {
  document.body.classList.toggle(b.dataset.toggle);
  b.setAttribute('aria-pressed', String(document.body.classList.contains(b.dataset.toggle)));
}));
