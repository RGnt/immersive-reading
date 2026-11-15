// The built-in colour themes of Settings -> Appearance. Shared by the settings screen and the contrast audit of --verify-ui.
export const THEME_PRESETS: Record<string, {
  bgPrimary: string;
  bgSurface: string;
  textPrimary: string;
  textMuted: string;
  borderColor: string;
  accentColor: string;
  rubyColor: string;
}> = {
  slate: {
    bgPrimary: '#0f172a',
    bgSurface: '#1e293b',
    textPrimary: '#f8fafc',
    textMuted: '#94a3b8',
    borderColor: '#334155',
    accentColor: '#38bdf8',
    rubyColor: '#e2e8f0',
  },
  'warm-paper': {
    bgPrimary: '#faf8f5',
    bgSurface: '#f2ece4',
    textPrimary: '#2d2720',
    textMuted: '#7c7267',
    borderColor: '#dcd3c6',
    accentColor: '#b45309',
    rubyColor: '#78350f',
  },
  'tokyo-night': {
    bgPrimary: '#1a1b26',
    bgSurface: '#24283b',
    textPrimary: '#c0caf5',
    textMuted: '#7aa2f7',
    borderColor: '#414868',
    accentColor: '#bb9af7',
    rubyColor: '#f7768e',
  },
  'e-ink': {
    bgPrimary: '#ffffff',
    bgSurface: '#f5f5f5',
    textPrimary: '#000000',
    textMuted: '#555555',
    borderColor: '#cccccc',
    accentColor: '#000000',
    rubyColor: '#333333',
  },
};
