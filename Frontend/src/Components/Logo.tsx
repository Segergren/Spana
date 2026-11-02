import { useId } from 'react';

// Spana mark: a circle split into three turning parts with thin seams (design/logo/spana-mark.svg).
export function LogoMark({ size = 28, className }: { size?: number; className?: string }) {
  const id = `spana-ap-${useId().replace(/:/g, '')}`;
  return (
    <svg viewBox="0 0 120 120" width={size} height={size} className={className} aria-hidden="true">
      <g transform="translate(60 60)"><defs><mask id={id} maskUnits="userSpaceOnUse" x="-64" y="-64" width="128" height="128"><rect x="-64" y="-64" width="128" height="128" fill="#fff"/><g fill="none" stroke="#000" strokeWidth="4.5" strokeLinecap="round"><path d="M0 0 A26 26 0 0 1 0.00 -50.00"/><path d="M0 0 A26 26 0 0 1 43.30 25.00"/><path d="M0 0 A26 26 0 0 1 -43.30 25.00"/></g><circle r="4.05" fill="#000"/></mask></defs><g mask={`url(#${id})`}><path fill="#14a896" d="M0 0 A26 26 0 0 1 0.00 -48.00 A48 48 0 0 1 41.57 24.00 A26 26 0 0 0 0 0 Z"/><path fill="#0c7d70" d="M0 0 A26 26 0 0 1 41.57 24.00 A48 48 0 0 1 -41.57 24.00 A26 26 0 0 0 0 0 Z"/><path fill="#5fd0bf" d="M0 0 A26 26 0 0 1 -41.57 24.00 A48 48 0 0 1 -0.00 -48.00 A26 26 0 0 0 0 0 Z"/></g></g>
    </svg>
  );
}

// Lifted half a pixel so the cap height centres on the mark (measured at 4x zoom).
export function Wordmark({ className }: { className?: string }) {
  return <span className={`font-brand inline-block -translate-y-[0.5px] ${className ?? ''}`}>Spana</span>;
}
