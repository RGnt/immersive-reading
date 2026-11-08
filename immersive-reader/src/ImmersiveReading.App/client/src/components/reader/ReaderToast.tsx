import React from 'react';
import { useReaderStore } from '../../stores/useReaderStore';

export const ReaderToast: React.FC = () => {
  const toastMessage = useReaderStore((s) => s.toastMessage);

  if (!toastMessage) return null;

  return (
    <div className="reader-toast pointer-events-none select-none animate-in fade-in zoom-in-95 duration-150">
      {toastMessage}
    </div>
  );
};

