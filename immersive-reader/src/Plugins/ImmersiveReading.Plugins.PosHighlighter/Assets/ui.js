import React from 'react';
import { Palette } from 'lucide-react';
import { usePluginRpc, setReaderStyle } from '@ir/sdk';

const PLUGIN_ID = 'posHighlighter';
// Same id the server stage injects, so the live CSS replaces it instead of stacking (spec 10 §4.4).
const STYLE_ID = 'ir-pos-highlighter-style';

const h = React.createElement;

// Quick presets, expressed in catalog ids (see PosCategoryCatalog.cs).
const PRESETS = [
  { label: 'Verbs', ids: ['verbGodan', 'verbIchidan', 'verbIrregular'] },
  { label: 'Adjectives', ids: ['adjI', 'adjNa'] },
  { label: 'Particles', ids: ['particleSingle', 'particleMulti'] },
  { label: 'Grammar words', ids: ['particleSingle', 'particleMulti', 'auxiliary', 'suffix', 'conjunction'] },
];

function PosMenu({ onClose }) {
  const { call } = usePluginRpc(PLUGIN_ID);
  const [state, setState] = React.useState(null); // { enabled, categories: [...] }
  const [error, setError] = React.useState('');
  // Serializes writes so rapid clicks cannot be applied out of order (latest selection wins).
  const chain = React.useRef(Promise.resolve());

  React.useEffect(() => {
    let alive = true;
    call('getCategories', {})
      .then((res) => alive && setState(res))
      .catch((e) => alive && setError(String(e?.message ?? e)));
    return () => {
      alive = false;
    };
  }, [call]);

  const push = (next, payload) => {
    const previous = state;
    setState(next); // optimistic
    setError('');
    chain.current = chain.current
      .then(() => call('setCategories', payload))
      .then((res) => setReaderStyle(PLUGIN_ID, STYLE_ID, res.css ?? ''))
      .catch((e) => {
        setState(previous); // roll back
        setError(String(e?.message ?? e));
      });
  };

  const setEnabledIds = (ids) => {
    const wanted = new Set(ids);
    push(
      { ...state, categories: state.categories.map((c) => ({ ...c, enabled: wanted.has(c.id) })) },
      { enabledCategories: [...wanted] }
    );
  };

  if (!state) {
    return h('div', { className: 'p-3 text-xs text-white/70' }, error || 'Loading…');
  }

  const selectedIds = state.categories.filter((c) => c.enabled).map((c) => c.id);
  const allIds = state.categories.map((c) => c.id);
  const disabled = !state.enabled;

  const chip = (label, ids) =>
    h(
      'button',
      {
        key: label,
        disabled,
        onClick: () => setEnabledIds(ids),
        className:
          'px-2 py-0.5 rounded border border-white/15 text-[11px] text-white/80 hover:bg-white/10 disabled:opacity-40 cursor-pointer',
      },
      label
    );

  return h(
    'div',
    { className: 'p-3 w-80 text-xs text-white select-none' },
    h(
      'div',
      { className: 'flex items-center justify-between mb-2' },
      h('span', { className: 'font-semibold' }, 'Highlight parts of speech'),
      h(
        'label',
        { className: 'flex items-center gap-1.5 cursor-pointer' },
        h('input', {
          type: 'checkbox',
          checked: state.enabled,
          onChange: (e) => push({ ...state, enabled: e.target.checked }, { enabled: e.target.checked }),
        }),
        'On'
      )
    ),
    h(
      'div',
      { className: 'flex flex-wrap gap-1 mb-2' },
      chip('All', allIds),
      chip('None', []),
      ...PRESETS.map((p) => chip(p.label, p.ids))
    ),
    h(
      'ul',
      { className: 'space-y-0.5 max-h-72 overflow-y-auto' },
      state.categories.map((c) =>
        h(
          'li',
          { key: c.id },
          h(
            'label',
            {
              className: `flex items-center gap-2 px-1.5 py-1 rounded hover:bg-white/10 cursor-pointer ${disabled ? 'opacity-40' : ''}`,
            },
            h('input', {
              type: 'checkbox',
              disabled,
              checked: c.enabled,
              onChange: (e) =>
                setEnabledIds(
                  e.target.checked ? [...selectedIds, c.id] : selectedIds.filter((id) => id !== c.id)
                ),
            }),
            h('span', {
              className: 'inline-block w-3 h-3 rounded-sm shrink-0',
              style: { backgroundColor: c.color },
              'aria-hidden': true,
            }),
            h('span', { className: 'flex-1' }, c.label, h('span', { className: 'text-white/50' }, ` ${c.jpLabel}`)),
            h('span', { className: 'text-white/50' }, c.example)
          )
        )
      )
    ),
    error && h('p', { className: 'mt-2 text-rose-300', role: 'alert' }, error),
    h(
      'div',
      { className: 'mt-2 flex justify-end' },
      h(
        'button',
        { onClick: onClose, className: 'px-2 py-0.5 rounded hover:bg-white/10 text-white/70 cursor-pointer' },
        'Close'
      )
    )
  );
}

export function activate(ctx) {
  // Reader top-bar "Highlights" menu (spec 10). Replaces the former inert on/off button.
  ctx.registerReaderToolbarAction({
    id: 'pos-highlighter-menu',
    label: 'Highlights',
    icon: Palette,
    popover: PosMenu,
  });
}

export function deactivate() {
  // Host unregisters the toolbar action and drops this plugin's live styles on disable.
}
