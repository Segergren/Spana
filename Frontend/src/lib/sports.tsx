import {
  Award,
  Bike,
  CarFront,
  CircleDot,
  Flag,
  Gauge,
  Goal,
  MountainSnow,
  Sailboat,
  Shield,
  Snowflake,
  Swords,
  Target,
  Timer,
  Trophy,
  Volleyball,
  Waves,
  type LucideIcon,
} from 'lucide-react';

// Flat card colours and icons per sport, used when a match has no broadcaster image.
const SPORTS: Record<string, { icon: LucideIcon; color: string }> = {
  Football: { icon: Goal, color: '#1d5a3c' },
  'American football': { icon: Goal, color: '#4a3a24' },
  'Ice hockey': { icon: Snowflake, color: '#1f4a72' },
  Handball: { icon: CircleDot, color: '#5c3a63' },
  Floorball: { icon: CircleDot, color: '#2f4f6e' },
  Basketball: { icon: CircleDot, color: '#7a4220' },
  Volleyball: { icon: Volleyball, color: '#6b5a24' },
  Tennis: { icon: CircleDot, color: '#566a1f' },
  Padel: { icon: CircleDot, color: '#3d6a4a' },
  Golf: { icon: Flag, color: '#2c6440' },
  Motorsport: { icon: Gauge, color: '#6e2330' },
  'Combat sports': { icon: Swords, color: '#55295f' },
  'Winter sports': { icon: MountainSnow, color: '#295a73' },
  Cycling: { icon: Bike, color: '#5f5226' },
  'Horse racing': { icon: Trophy, color: '#5f3f28' },
  Equestrian: { icon: Trophy, color: '#5f3f28' },
  Snooker: { icon: Target, color: '#1f5047' },
  Darts: { icon: Target, color: '#5a2a2a' },
  Athletics: { icon: Timer, color: '#433a72' },
  Bandy: { icon: Snowflake, color: '#24506a' },
  Esports: { icon: CarFront, color: '#3a3a6e' },
  Baseball: { icon: CircleDot, color: '#3b4f7a' },
  Rugby: { icon: Shield, color: '#4f3a2a' },
  Cricket: { icon: Award, color: '#3f5a2a' },
  'Table tennis': { icon: CircleDot, color: '#5a2f3f' },
  Badminton: { icon: CircleDot, color: '#4a5a2a' },
  'Field hockey': { icon: Goal, color: '#2f5a3f' },
  'Australian football': { icon: Goal, color: '#5a3f24' },
  'Gaelic games': { icon: Goal, color: '#2a5a4a' },
  Swimming: { icon: Waves, color: '#1f4f6e' },
  Sailing: { icon: Sailboat, color: '#24506a' },
};

const FALLBACK = { icon: Trophy, color: '#283544' };

export function sportStyle(sport?: string) {
  return (sport && SPORTS[sport]) || FALLBACK;
}
