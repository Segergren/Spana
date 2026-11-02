import { useEffect, useRef } from 'react';
import { registerSlot, type SlotMode } from '../lib/stores';

export default function VideoSlot({ mode, className }: { mode: SlotMode; className?: string }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => (ref.current ? registerSlot(ref.current, mode) : undefined), [mode]);
  return <div ref={ref} className={className} />;
}
