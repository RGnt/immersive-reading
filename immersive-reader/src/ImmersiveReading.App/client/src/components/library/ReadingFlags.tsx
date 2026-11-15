import React from 'react';
import { Star } from 'lucide-react';
import type { ReadingStatus } from '../../types/api';

export const STATUS_LABELS: Record<Exclude<ReadingStatus, ''>, string> = {
  reading: 'Currently reading',
  planned: 'Planning to read',
  completed: 'Completed',
};

/** Short names for the card chip. */
export const STATUS_SHORT: Record<Exclude<ReadingStatus, ''>, string> = {
  reading: 'Reading',
  planned: 'Planned',
  completed: 'Done',
};

/**
 * A star. Filled in the accent colour when the flag is the thing's own; an outline star in a softer fill when it is only inherited
 * (a book of a favourite series); an empty outline otherwise.
 */
export const FavoriteStar: React.FC<{
  own: boolean;
  inherited?: boolean;
  onToggle: () => void;
  title?: string;
  size?: number;
}> = ({ own, inherited = false, onToggle, title, size = 14 }) => (
  <button
    type="button"
    data-testid="favorite-star"
    aria-pressed={own}
    title={title ?? (own ? 'Remove from favourites' : inherited ? 'In a favourite series. Click to favourite this book too' : 'Add to favourites')}
    onClick={(e) => {
      e.stopPropagation();
      onToggle();
    }}
    onDoubleClick={(e) => e.stopPropagation()}
    className={`shrink-0 p-0.5 transition-colors ${
      own ? 'text-[var(--accent-text)]' : inherited ? 'text-[var(--accent-text)]/60' : 'text-[var(--text-muted)] hover:text-[var(--accent-text)]'
    }`}
  >
    <Star width={size} height={size} fill={own ? 'currentColor' : inherited ? 'currentColor' : 'none'} fillOpacity={own ? 1 : 0.3} />
  </button>
);

/** The reading list choice of a book or a series. `inherited` is the series' status shown as the fallback of a book that has none of its own. */
export const StatusSelect: React.FC<{
  value: ReadingStatus;
  onChange: (status: ReadingStatus) => void;
  inherited?: ReadingStatus;
  testId?: string;
  className?: string;
}> = ({ value, onChange, inherited = '', testId, className = '' }) => (
  <select
    data-testid={testId}
    value={value}
    onClick={(e) => e.stopPropagation()}
    onChange={(e) => onChange(e.target.value as ReadingStatus)}
    className={`px-2 py-1 bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] ${className}`}
  >
    <option value="">{inherited ? `Not set (series: ${STATUS_LABELS[inherited as Exclude<ReadingStatus, ''>]})` : 'Not on a list'}</option>
    {(Object.keys(STATUS_LABELS) as Exclude<ReadingStatus, ''>[]).map((s) => (
      <option key={s} value={s}>{STATUS_LABELS[s]}</option>
    ))}
  </select>
);
